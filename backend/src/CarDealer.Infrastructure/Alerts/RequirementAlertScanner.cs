using CarDealer.Application.Abstractions;
using CarDealer.Application.Search;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.Alerts;

/// <summary>
/// Finds cars that arrived after a customer asked for them, and raises an alert for each.
/// </summary>
/// <remarks>
/// Runs for one tenant at a time, in a scope where that tenant is resolved so the DbContext's
/// filters do the isolation rather than a <c>Where</c> clause somebody can forget.
///
/// <para>
/// <b>Matching reuses <see cref="ISearchProvider"/>.</b> There is exactly one place in this
/// codebase that decides what a requirement matches, and it is the same one the requirement's
/// own matches list and the vehicle search screen use. A second implementation here would drift
/// from that one, and the drift would be invisible: the alert and the list would quietly
/// disagree about the same customer.
/// </para>
///
/// <para>
/// <b>Alerts ignore per-user muted sources, deliberately.</b> The search provider only applies
/// the mute filter when a user is resolved, and a background scan has no user - so this sees
/// the tenant's whole catalogue. That is the behaviour to want, not an accident of the scope:
/// muting a source is a browsing preference, and letting one salesperson's preference suppress
/// an alert about a car a customer is waiting for would lose a sale for a reason nobody could
/// see afterwards.
/// </para>
/// </remarks>
public sealed class RequirementAlertScanner
{
    private readonly CarDealerDbContext _db;
    private readonly ISearchProvider _search;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<RequirementAlertScanner> _log;

    /// <summary>
    /// The most matches one requirement can raise in a single scan.
    /// </summary>
    /// <remarks>
    /// A backstop, not a design point: the "arrived after you asked" rule already keeps a scan
    /// down to genuinely new stock. This bounds the damage if somebody imports a thirty-thousand
    /// car feed against a requirement whose only criterion is "a Toyota".
    /// </remarks>
    private const int MaxAlertsPerRequirementPerScan = 50;

    public RequirementAlertScanner(
        CarDealerDbContext db,
        ISearchProvider search,
        IDateTimeProvider clock,
        ILogger<RequirementAlertScanner> log)
    {
        _db = db;
        _search = search;
        _clock = clock;
        _log = log;
    }

    /// <summary>Scans every open requirement of the currently resolved tenant.</summary>
    public async Task<AlertScanResult> ScanAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        var requirements = await _db.CustomerRequirements
            .AsNoTracking()
            .Where(r => r.Status == RequirementStatus.Open)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var raised = 0;

        foreach (var requirement in requirements)
        {
            raised += await ScanOneAsync(requirement, now, ct).ConfigureAwait(false);
        }

        if (raised > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new AlertScanResult(requirements.Count, raised);
    }

    private async Task<int> ScanOneAsync(
        CustomerRequirement requirement, DateTime now, CancellationToken ct)
    {
        var result = await _search
            .SearchAsync(ToQuery(requirement), ct)
            .ConfigureAwait(false);

        if (result.Hits.Count == 0)
        {
            return 0;
        }

        // Search returns public ids, because that is what a URL carries. The alert's foreign key
        // needs the row key, so they are translated here rather than by widening the search
        // projection - nothing outside this file has a reason to know a vehicle's sequential id.
        var publicIds = result.Hits.Select(h => h.PublicId).ToList();

        var keysByPublicId = await _db.Vehicles
            .AsNoTracking()
            .Where(v => publicIds.Contains(v.PublicId))
            .Select(v => new { v.Id, v.PublicId })
            .ToDictionaryAsync(v => v.PublicId, v => v.Id, ct)
            .ConfigureAwait(false);

        // Which of these are already known, so a re-scan raises nothing. The unique index would
        // catch a duplicate anyway, but as a DbUpdateException part-way through a batch rather
        // than as a no-op.
        var candidates = keysByPublicId.Values.ToList();

        var known = (await _db.RequirementAlerts
            .AsNoTracking()
            .Where(a => a.CustomerRequirementId == requirement.Id
                && candidates.Contains(a.VehicleId))
            .Select(a => a.VehicleId)
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .ToHashSet();

        var raised = 0;

        foreach (var hit in result.Hits)
        {
            // A car deleted between the search and this lookup simply is not alerted on.
            if (!keysByPublicId.TryGetValue(hit.PublicId, out var vehicleId)
                || known.Contains(vehicleId)
                || raised >= MaxAlertsPerRequirementPerScan)
            {
                continue;
            }

            _db.RequirementAlerts.Add(new RequirementAlert
            {
                TenantId = requirement.TenantId,
                CustomerRequirementId = requirement.Id,
                VehicleId = vehicleId,
                MatchedAtUtc = now,
                PriceBaseAtMatch = hit.PriceBaseCurrency,
                BaseCurrencyCode = hit.BaseCurrencyCode,
            });

            known.Add(vehicleId);
            raised++;
        }

        if (raised > 0)
        {
            _log.LogInformation(
                "Requirement {RequirementId} raised {Count} alert(s).", requirement.Id, raised);
        }

        return raised;
    }

    /// <summary>
    /// The requirement as a search, narrowed to stock that arrived after it was written.
    /// </summary>
    /// <remarks>
    /// <see cref="VehicleSearchQuery.ListedAfterUtc"/> is what makes this an alert rather than a
    /// second copy of the matches list. Without it, writing a requirement against a catalogue of
    /// a hundred cars would raise an alert for every one that fits - forty notifications about
    /// stock the salesperson is already looking at.
    ///
    /// Sorted newest-first so that if a scan does hit the per-requirement cap, what survives is
    /// the freshest stock rather than an arbitrary slice.
    /// </remarks>
    private static VehicleSearchQuery ToQuery(CustomerRequirement r) => new()
    {
        Make = r.Make,
        Model = r.Model,
        BodyType = r.BodyType,
        Text = r.Variant,
        MinYear = r.MinYear,
        MaxYear = r.MaxYear,
        MinMileage = r.MinMileage,
        MaxMileage = r.MaxMileage,
        Transmission = r.Transmission,
        FuelType = r.FuelType,
        MinPriceBase = r.MinPrice,
        MaxPriceBase = r.MaxPrice,
        ListedAfterUtc = r.CreatedAtUtc,
        Page = 1,
        PageSize = MaxAlertsPerRequirementPerScan,
        Sort = VehicleSearchSort.RecentlySeen,
    };
}

/// <param name="RequirementsScanned">Open requirements considered.</param>
/// <param name="AlertsRaised">New alerts written. Zero on a re-scan with no new stock.</param>
public sealed record AlertScanResult(int RequirementsScanned, int AlertsRaised);
