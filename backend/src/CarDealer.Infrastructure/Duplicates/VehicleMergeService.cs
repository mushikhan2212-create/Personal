using System.Text.Json;
using CarDealer.Application.Abstractions;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Infrastructure.Duplicates;

/// <summary>
/// Carries out a reviewer's decision on a suggested duplicate, and undoes it.
/// </summary>
/// <remarks>
/// <para>
/// A merge repoints the duplicate's listings and photos onto the survivor and archives the
/// duplicate. It never deletes: search already excludes <see cref="VehicleStatus.Archived"/>,
/// so archiving is enough to take the second copy off the screen, and keeping the row is what
/// makes the merge reversible.
/// </para>
///
/// <para>
/// <b>Reversibility is not a nicety here.</b> Under decision D1 the catalogue is global, so a
/// wrong merge is wrong for every tenant at once, and the person who notices is usually not the
/// person who did it. Every merge records the exact listing and image ids it moved, because
/// "put it back" is otherwise guesswork - the survivor may legitimately have acquired other
/// listings since, and moving those back would break a merge that was correct.
/// </para>
///
/// <para>
/// <b>The survivor is the older row.</b> Not the cheaper one, and not the one with more
/// photos: whichever vehicle has been in the catalogue longer is the one other records already
/// point at - requirement alerts, and any tenant's saved view of it. Choosing the newcomer
/// would orphan those to archive an id people have already seen.
/// </para>
/// </remarks>
public sealed class VehicleMergeService
{
    private readonly CarDealerDbContext _db;
    private readonly IDateTimeProvider _clock;

    public VehicleMergeService(CarDealerDbContext db, IDateTimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Merges the pair behind a candidate, returning the surviving vehicle.
    /// </summary>
    public async Task<MergeOutcome> MergeAsync(
        long candidateId, long reviewerUserId, string? note, CancellationToken ct = default)
    {
        var candidate = await _db.VehicleMatchCandidates
            .FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            .ConfigureAwait(false);

        if (candidate is null)
        {
            return MergeOutcome.NotFound;
        }

        if (candidate.Status != MatchCandidateStatus.Pending)
        {
            // Somebody else got there first. Reported rather than repeated, because merging an
            // already-merged pair would archive a vehicle whose listings have already moved.
            return MergeOutcome.AlreadyReviewed;
        }

        var vehicles = await _db.Vehicles
            .IgnoreQueryFilters()
            .Where(v => v.Id == candidate.VehicleId || v.Id == candidate.CandidateVehicleId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (vehicles.Count != 2)
        {
            return MergeOutcome.NotFound;
        }

        // Belt and braces over the scan's own grouping: this is the last point before two rows
        // become one, and a cross-tenant merge is the failure D1 exists to prevent.
        if (vehicles[0].TenantId != vehicles[1].TenantId)
        {
            return MergeOutcome.DifferentOwners;
        }

        var survivor = vehicles.OrderBy(v => v.CreatedAtUtc).ThenBy(v => v.Id).First();
        var absorbed = vehicles.First(v => v.Id != survivor.Id);

        var listings = await _db.VehicleListings
            .IgnoreQueryFilters()
            .Where(l => l.VehicleId == absorbed.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var images = await _db.VehicleImages
            .IgnoreQueryFilters()
            .Where(i => i.VehicleId == absorbed.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var listing in listings)
        {
            listing.VehicleId = survivor.Id;
        }

        foreach (var image in images)
        {
            image.VehicleId = survivor.Id;
        }

        var (movedAlerts, droppedAlerts) = await RepointAlertsAsync(survivor.Id, absorbed.Id, ct)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;

        absorbed.Status = VehicleStatus.Archived;
        absorbed.UpdatedAtUtc = now;

        _db.VehicleMergeHistories.Add(new VehicleMergeHistory
        {
            SurvivingVehicleId = survivor.Id,
            MergedVehicleId = absorbed.Id,
            MergedByUserId = reviewerUserId,
            ReasonsJson = JsonSerializer.Serialize(new
            {
                rule = "reviewed",
                candidateId = candidate.Id,
                score = candidate.Score,
                signals = candidate.SignalsJson,
                note,
            }),
            RepointedListingIdsJson = JsonSerializer.Serialize(new
            {
                listings = listings.Select(l => l.Id).ToArray(),
                images = images.Select(i => i.Id).ToArray(),
                alerts = movedAlerts,
                alertsDropped = droppedAlerts,
            }),
            MergedAtUtc = now,
        });

        candidate.Status = MatchCandidateStatus.Merged;
        candidate.ReviewedByUserId = reviewerUserId;
        candidate.ReviewedAtUtc = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return MergeOutcome.Merged(survivor.PublicId, absorbed.PublicId, listings.Count, images.Count);
    }

    /// <summary>
    /// Moves the duplicate's "a car you were waiting for arrived" alerts onto the survivor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the point of the whole feature from a salesperson's side. A car listed twice
    /// raises <em>two</em> alerts for the same customer - the scan cannot tell they are one car,
    /// which is precisely the problem being fixed - and merging without touching them leaves the
    /// pair sitting in the inbox alongside an alert whose vehicle now has no offers on it,
    /// because the offers moved.
    /// </para>
    ///
    /// <para>
    /// A requirement may already have an alert about the survivor, and the unique index on
    /// (requirement, vehicle) says so. That one is the same news, so the duplicate is deleted
    /// rather than repointed - which is the double alert going away. Reversing the merge cannot
    /// bring it back; the customer is left with one alert about a car they are still told about,
    /// which is the right end state either way.
    /// </para>
    /// </remarks>
    private async Task<(long[] Moved, int Dropped)> RepointAlertsAsync(
        long survivorId, long absorbedId, CancellationToken ct)
    {
        // IgnoreQueryFilters because this runs with no tenant resolved: every tenant's alerts
        // about the absorbed car have to move, not just one's.
        var alerts = await _db.RequirementAlerts
            .IgnoreQueryFilters()
            .Where(a => a.VehicleId == absorbedId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (alerts.Count == 0)
        {
            return ([], 0);
        }

        var requirementIds = alerts.Select(a => a.CustomerRequirementId).ToList();

        var alreadyAboutSurvivor = await _db.RequirementAlerts
            .IgnoreQueryFilters()
            .Where(a => a.VehicleId == survivorId && requirementIds.Contains(a.CustomerRequirementId))
            .Select(a => a.CustomerRequirementId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var covered = alreadyAboutSurvivor.ToHashSet();

        var moved = new List<long>();
        var dropped = 0;

        foreach (var alert in alerts)
        {
            if (covered.Add(alert.CustomerRequirementId))
            {
                alert.VehicleId = survivorId;
                moved.Add(alert.Id);
            }
            else
            {
                _db.RequirementAlerts.Remove(alert);
                dropped++;
            }
        }

        return ([.. moved], dropped);
    }

    /// <summary>Records that a reviewer says the pair is two different cars.</summary>
    /// <remarks>
    /// The row stays, with status Rejected, and that is the point: the scan reads it and does
    /// not offer the pair again. Deleting it would put the same suggestion back in the queue on
    /// the next run, which is how a review queue trains people to ignore it.
    /// </remarks>
    public async Task<bool> RejectAsync(
        long candidateId, long reviewerUserId, CancellationToken ct = default)
    {
        var candidate = await _db.VehicleMatchCandidates
            .FirstOrDefaultAsync(c => c.Id == candidateId && c.Status == MatchCandidateStatus.Pending, ct)
            .ConfigureAwait(false);

        if (candidate is null)
        {
            return false;
        }

        candidate.Status = MatchCandidateStatus.Rejected;
        candidate.ReviewedByUserId = reviewerUserId;
        candidate.ReviewedAtUtc = _clock.UtcNow;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Undoes a merge, putting the recorded listings and images back and un-archiving the
    /// vehicle.
    /// </summary>
    /// <remarks>
    /// Only the ids the merge itself moved, read back from the history row. Anything the
    /// survivor gained afterwards stays where it is - a later sync attaching a third exporter's
    /// listing to the survivor is not part of this merge and must not be dragged out by
    /// reversing it.
    /// </remarks>
    public async Task<bool> RevertAsync(
        long mergeHistoryId, long reviewerUserId, CancellationToken ct = default)
    {
        var history = await _db.VehicleMergeHistories
            .FirstOrDefaultAsync(h => h.Id == mergeHistoryId && h.RevertedAtUtc == null, ct)
            .ConfigureAwait(false);

        if (history is null || history.SurvivingVehicleId == history.MergedVehicleId)
        {
            // A history row whose two ids are equal is the automatic strong-identifier path,
            // which recorded that a listing attached to an existing vehicle rather than that
            // two vehicles became one. There is nothing to pull apart.
            return false;
        }

        var moved = Moved(history.RepointedListingIdsJson);

        var listings = await _db.VehicleListings
            .IgnoreQueryFilters()
            .Where(l => moved.Listings.Contains(l.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var images = await _db.VehicleImages
            .IgnoreQueryFilters()
            .Where(i => moved.Images.Contains(i.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var listing in listings)
        {
            listing.VehicleId = history.MergedVehicleId;
        }

        foreach (var image in images)
        {
            image.VehicleId = history.MergedVehicleId;
        }

        // Only the alerts this merge moved. One deleted as a duplicate stays deleted - it said
        // the same thing as the one that survived, and the customer is still being told.
        var alerts = await _db.RequirementAlerts
            .IgnoreQueryFilters()
            .Where(a => moved.Alerts.Contains(a.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var alert in alerts)
        {
            alert.VehicleId = history.MergedVehicleId;
        }

        var absorbed = await _db.Vehicles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.Id == history.MergedVehicleId, ct)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;

        if (absorbed is not null)
        {
            absorbed.Status = VehicleStatus.Active;
            absorbed.UpdatedAtUtc = now;
        }

        history.RevertedAtUtc = now;
        history.RevertedByUserId = reviewerUserId;

        // Back to Pending rather than Rejected: reversing a merge says it was wrong to merge,
        // not that the two cars are definitely different, and the reviewer who undid it may
        // well want to look again.
        var candidate = await _db.VehicleMatchCandidates
            .FirstOrDefaultAsync(
                c => c.VehicleId == Math.Min(history.SurvivingVehicleId, history.MergedVehicleId)
                    && c.CandidateVehicleId == Math.Max(history.SurvivingVehicleId, history.MergedVehicleId),
                ct)
            .ConfigureAwait(false);

        if (candidate is not null)
        {
            candidate.Status = MatchCandidateStatus.Pending;
            candidate.ReviewedByUserId = null;
            candidate.ReviewedAtUtc = null;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    private static (long[] Listings, long[] Images, long[] Alerts) Moved(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ([], [], []);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return (Ids(root, "listings"), Ids(root, "images"), Ids(root, "alerts"));
        }
        catch (JsonException)
        {
            // A merge recorded before this field existed, or a corrupt blob. Reversing what can
            // be read is better than refusing: the vehicle still comes back out of archive.
            return ([], [], []);
        }

        static long[] Ids(JsonElement root, string name)
            => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
                ? [.. array.EnumerateArray().Select(e => e.GetInt64())]
                : [];
    }
}

/// <summary>The result of acting on a candidate.</summary>
public sealed record MergeOutcome(
    MergeStatus Status,
    Guid? SurvivingPublicId = null,
    Guid? ArchivedPublicId = null,
    int ListingsMoved = 0,
    int ImagesMoved = 0)
{
    public static readonly MergeOutcome NotFound = new(MergeStatus.NotFound);
    public static readonly MergeOutcome AlreadyReviewed = new(MergeStatus.AlreadyReviewed);
    public static readonly MergeOutcome DifferentOwners = new(MergeStatus.DifferentOwners);

    public static MergeOutcome Merged(Guid survivor, Guid archived, int listings, int images)
        => new(MergeStatus.Merged, survivor, archived, listings, images);
}

public enum MergeStatus
{
    Merged,
    NotFound,
    AlreadyReviewed,
    DifferentOwners,
}
