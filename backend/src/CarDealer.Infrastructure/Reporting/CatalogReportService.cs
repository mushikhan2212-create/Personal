using System.Diagnostics;
using CarDealer.Application.Abstractions;
using CarDealer.Application.Search;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Infrastructure.Reporting;

/// <summary>
/// Measures the catalogue against master prompt section 8's POC criteria.
/// </summary>
/// <remarks>
/// Section 8 asks the POC to report "data completeness, freshness, response time, duplicates,
/// images, quotas and cost". Those are measurements, and a measurement written by hand into a
/// document is out of date the moment the next import runs. This computes them from whatever
/// the catalogue currently holds, so the report can be regenerated rather than rewritten.
///
/// It also answers the question the POC exists to answer, which is not flattering and is the
/// point: with Japanese export stock carrying chassis numbers rather than VINs, how much can
/// deduplication actually do? <see cref="DeduplicationReport"/> reports what it managed and
/// what it could not attempt.
/// </remarks>
public sealed class CatalogReportService
{
    private readonly CarDealerDbContext _db;
    private readonly ISearchProvider _search;
    private readonly IDateTimeProvider _clock;

    /// <summary>Past this many days a listing's availability should not be trusted.</summary>
    private const int StaleAfterDays = 14;

    public CatalogReportService(
        CarDealerDbContext db, ISearchProvider search, IDateTimeProvider clock)
    {
        _db = db;
        _search = search;
        _clock = clock;
    }

    public async Task<CatalogReport> BuildAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        return new CatalogReport(
            GeneratedAtUtc: now,
            Totals: await TotalsAsync(ct).ConfigureAwait(false),
            Sources: await SourcesAsync(now, ct).ConfigureAwait(false),
            Deduplication: await DeduplicationAsync(ct).ConfigureAwait(false),
            Searches: await SearchTimingsAsync(ct).ConfigureAwait(false));
    }

    private async Task<CatalogTotals> TotalsAsync(CancellationToken ct)
    {
        var vehicles = await _db.Vehicles.CountAsync(ct).ConfigureAwait(false);
        var listings = await _db.VehicleListings.CountAsync(l => l.IsActive, ct).ConfigureAwait(false);
        var sources = await _db.VehicleSources.CountAsync(s => s.IsActive, ct).ConfigureAwait(false);
        var images = await _db.VehicleImages.CountAsync(ct).ConfigureAwait(false);

        return new CatalogTotals(vehicles, listings, sources, images);
    }

    /// <summary>
    /// Per-source field completeness, freshness and identifier coverage.
    /// </summary>
    /// <remarks>
    /// One grouped query rather than a scalar query per field per source: the same numbers, but
    /// a single round trip instead of dozens, and SQL Server computes the conditional sums as
    /// part of the aggregate it is already doing.
    ///
    /// Completeness is measured over listings, not vehicles, because a field is supplied by a
    /// source - saying "82% of vehicles have a colour" hides that one source supplies it always
    /// and another never does, which is exactly the comparison this table exists to make.
    /// </remarks>
    private async Task<IReadOnlyList<SourceReport>> SourcesAsync(DateTime now, CancellationToken ct)
    {
        var staleBefore = now.AddDays(-StaleAfterDays);

        var rows = await _db.VehicleListings
            .Where(l => l.IsActive)
            .GroupBy(l => l.VehicleSourceId)
            .Select(g => new
            {
                VehicleSourceId = g.Key,
                Listings = g.Count(),

                // Identity. The number that decides whether deduplication is even possible.
                WithVin = g.Sum(l => l.Vehicle.Vin != null ? 1 : 0),
                WithChassis = g.Sum(l => l.Vehicle.ChassisNumber != null ? 1 : 0),
                WithLot = g.Sum(l => l.Vehicle.LotNumber != null ? 1 : 0),
                // Deliberately ignores the lot number. It is the exporter's own stock code,
                // hashed with the source id, so it identifies this car inside this source and
                // nowhere else - counting it here would report a catalogue as fully identified
                // while no car in it can be recognised in another source's data.
                WithoutCrossSourceId = g.Sum(l => l.Vehicle.Vin == null
                    && l.Vehicle.ChassisNumber == null ? 1 : 0),

                // Specification.
                WithMake = g.Sum(l => l.Vehicle.Make != null ? 1 : 0),
                WithModel = g.Sum(l => l.Vehicle.Model != null ? 1 : 0),
                WithVariant = g.Sum(l => l.Vehicle.Variant != null ? 1 : 0),
                WithYear = g.Sum(l => l.Vehicle.ModelYear != null ? 1 : 0),
                WithMileage = g.Sum(l => l.Vehicle.Mileage != null ? 1 : 0),
                WithBody = g.Sum(l => l.Vehicle.BodyType != null ? 1 : 0),
                WithColour = g.Sum(l => l.Vehicle.ExteriorColor != null ? 1 : 0),
                WithEngineCc = g.Sum(l => l.Vehicle.EngineDisplacementCc != null ? 1 : 0),
                WithFuel = g.Sum(l => l.Vehicle.FuelType != FuelType.Unknown ? 1 : 0),
                WithTransmission = g.Sum(l => l.Vehicle.Transmission != Transmission.Unknown ? 1 : 0),
                WithSteering = g.Sum(l => l.Vehicle.SteeringSide != SteeringSide.Unknown ? 1 : 0),

                // Commercial. A price with no incoterm cannot be compared with one that has it,
                // so the two are counted separately rather than as one "has a price" number.
                WithPrice = g.Sum(l => l.Price != null ? 1 : 0),
                WithIncoterm = g.Sum(l => l.PriceType != PriceType.Unknown ? 1 : 0),
                WithBasePrice = g.Sum(l => l.PriceBaseCurrency != null ? 1 : 0),
                WithPort = g.Sum(l => l.PortOfLoading != null ? 1 : 0),
                WithUrl = g.Sum(l => l.SourceUrl != null ? 1 : 0),

                // Freshness.
                Stale = g.Sum(l => l.LastSeenAtUtc < staleBefore ? 1 : 0),
                OldestSeenUtc = g.Min(l => l.LastSeenAtUtc),
                NewestSeenUtc = g.Max(l => l.LastSeenAtUtc),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var sources = await _db.VehicleSources
            .Select(s => new { s.Id, s.Code, s.Name, s.ProviderType })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Photo coverage is counted over vehicles rather than listings: images belong to the
        // car, and a car offered twice does not have twice the photographs.
        var withPhotos = await _db.VehicleListings
            .Where(l => l.IsActive)
            .GroupBy(l => l.VehicleSourceId)
            .Select(g => new
            {
                VehicleSourceId = g.Key,
                Count = g.Select(l => l.VehicleId).Distinct().Count(),
                Photographed = g.Where(l => l.Vehicle.Images.Any())
                    .Select(l => l.VehicleId).Distinct().Count(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows
            .Select(r =>
            {
                var source = sources.FirstOrDefault(s => s.Id == r.VehicleSourceId);
                var photos = withPhotos.FirstOrDefault(p => p.VehicleSourceId == r.VehicleSourceId);
                var total = r.Listings;

                return new SourceReport(
                    Code: source?.Code ?? $"source-{r.VehicleSourceId}",
                    Name: source?.Name ?? "(deleted source)",
                    ProviderType: source?.ProviderType.ToString() ?? "Unknown",
                    Listings: total,
                    Vehicles: photos?.Count ?? 0,
                    Identity: new IdentityCoverage(
                        Percent(r.WithVin, total),
                        Percent(r.WithChassis, total),
                        Percent(r.WithLot, total),
                        Percent(r.WithoutCrossSourceId, total)),
                    Completeness: new Dictionary<string, double>
                    {
                        ["make"] = Percent(r.WithMake, total),
                        ["model"] = Percent(r.WithModel, total),
                        ["variant"] = Percent(r.WithVariant, total),
                        ["year"] = Percent(r.WithYear, total),
                        ["mileage"] = Percent(r.WithMileage, total),
                        ["bodyType"] = Percent(r.WithBody, total),
                        ["exteriorColour"] = Percent(r.WithColour, total),
                        ["engineCc"] = Percent(r.WithEngineCc, total),
                        ["fuelType"] = Percent(r.WithFuel, total),
                        ["transmission"] = Percent(r.WithTransmission, total),
                        ["steering"] = Percent(r.WithSteering, total),
                        ["price"] = Percent(r.WithPrice, total),
                        ["incoterm"] = Percent(r.WithIncoterm, total),
                        ["comparableBasePrice"] = Percent(r.WithBasePrice, total),
                        ["portOfLoading"] = Percent(r.WithPort, total),
                        ["listingUrl"] = Percent(r.WithUrl, total),
                        ["photos"] = Percent(photos?.Photographed ?? 0, photos?.Count ?? 0),
                    },
                    Freshness: new FreshnessReport(
                        StalePercent: Percent(r.Stale, total),
                        OldestSeenUtc: r.OldestSeenUtc,
                        NewestSeenUtc: r.NewestSeenUtc));
            })
            .OrderByDescending(s => s.Listings)];
    }

    private async Task<DeduplicationReport> DeduplicationAsync(CancellationToken ct)
    {
        // How many cars are offered more than once, and by how many distinct sources. The
        // second number is the one that matters: two listings from one exporter is that
        // exporter listing a car twice, which is not what cross-source deduplication is for.
        var grouped = await _db.VehicleListings
            .Where(l => l.IsActive)
            .GroupBy(l => l.VehicleId)
            .Select(g => new
            {
                Listings = g.Count(),
                Sources = g.Select(l => l.VehicleSourceId).Distinct().Count(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var vehiclesWithVin = await _db.Vehicles
            .CountAsync(v => v.Vin != null, ct)
            .ConfigureAwait(false);

        var vehiclesWithChassis = await _db.Vehicles
            .CountAsync(v => v.Vin == null && v.ChassisNumber != null, ct)
            .ConfigureAwait(false);

        var vehiclesWithNeither = await _db.Vehicles
            .CountAsync(v => v.Vin == null && v.ChassisNumber == null, ct)
            .ConfigureAwait(false);

        var merges = await _db.VehicleMergeHistories.CountAsync(ct).ConfigureAwait(false);

        return new DeduplicationReport(
            VehiclesTotal: grouped.Count,
            OfferedMoreThanOnce: grouped.Count(g => g.Listings > 1),
            OfferedByMoreThanOneSource: grouped.Count(g => g.Sources > 1),
            AutoMergesRecorded: merges,
            IdentifiableByVin: vehiclesWithVin,
            IdentifiableByChassisOnly: vehiclesWithChassis,
            NoStrongIdentifier: vehiclesWithNeither);
    }

    /// <summary>
    /// Times the five realistic searches master prompt section 8 requires.
    /// </summary>
    /// <remarks>
    /// Run through <see cref="ISearchProvider"/> rather than against the tables directly, so
    /// what is measured is what the screen actually does - filters, grouping by car and the
    /// per-user source preference included. A timing taken from a hand-written query would
    /// flatter the product by measuring something nobody runs.
    ///
    /// Each search runs twice and the second is reported: the first pays for the query plan and
    /// a cold buffer pool, which is a real cost but not the one a user experiences all day.
    /// </remarks>
    private async Task<IReadOnlyList<SearchTiming>> SearchTimingsAsync(CancellationToken ct)
    {
        (string Label, VehicleSearchQuery Query)[] searches =
        [
            ("Everything, first page", new VehicleSearchQuery { PageSize = 25 }),
            ("Free text: 'toyota'", new VehicleSearchQuery { Text = "toyota", PageSize = 25 }),
            ("Right-hand drive, 2018 or newer", new VehicleSearchQuery
            {
                SteeringSide = SteeringSide.RightHandDrive, MinYear = 2018, PageSize = 25,
            }),
            ("Diesel under 100,000 km", new VehicleSearchQuery
            {
                FuelType = FuelType.Diesel, MaxMileage = 100_000, PageSize = 25,
            }),
            ("Cheapest first", new VehicleSearchQuery
            {
                Sort = VehicleSearchSort.PriceAscending, PageSize = 25,
            }),
        ];

        var results = new List<SearchTiming>(searches.Length);

        foreach (var (label, query) in searches)
        {
            await _search.SearchAsync(query, ct).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            var result = await _search.SearchAsync(query, ct).ConfigureAwait(false);
            stopwatch.Stop();

            results.Add(new SearchTiming(
                label,
                result.TotalCount,
                (int)stopwatch.ElapsedMilliseconds));
        }

        return results;
    }

    private static double Percent(int part, int whole)
        => whole == 0 ? 0 : Math.Round(part * 100.0 / whole, 1);
}

public sealed record CatalogReport(
    DateTime GeneratedAtUtc,
    CatalogTotals Totals,
    IReadOnlyList<SourceReport> Sources,
    DeduplicationReport Deduplication,
    IReadOnlyList<SearchTiming> Searches);

public sealed record CatalogTotals(int Vehicles, int ActiveListings, int Sources, int Images);

public sealed record SourceReport(
    string Code,
    string Name,
    string ProviderType,
    int Listings,
    int Vehicles,
    IdentityCoverage Identity,
    IReadOnlyDictionary<string, double> Completeness,
    FreshnessReport Freshness);

/// <summary>
/// What share of a source's listings carry each kind of identifier, and - the number that
/// decides whether aggregating several exporters can work at all - what share carry none that
/// would recognise the same car in another source.
/// </summary>
/// <param name="SourceLotOnlyPercent">
/// Carries the exporter's own stock code and nothing stronger. Useful for re-importing this
/// source without creating duplicates of itself, useless for matching against any other.
/// </param>
public sealed record IdentityCoverage(
    double VinPercent,
    double ChassisPercent,
    double SourceLotOnlyPercent,
    double NoCrossSourceIdentifierPercent);

public sealed record FreshnessReport(
    double StalePercent,
    DateTime OldestSeenUtc,
    DateTime NewestSeenUtc);

/// <summary>
/// What deduplication managed, and what it was never able to attempt.
/// </summary>
public sealed record DeduplicationReport(
    int VehiclesTotal,
    int OfferedMoreThanOnce,
    int OfferedByMoreThanOneSource,
    int AutoMergesRecorded,
    int IdentifiableByVin,
    int IdentifiableByChassisOnly,
    int NoStrongIdentifier);

public sealed record SearchTiming(string Label, int Matches, int ElapsedMs);
