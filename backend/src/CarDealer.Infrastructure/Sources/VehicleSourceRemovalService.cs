using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.Sources;

/// <summary>What removing a source took with it.</summary>
public sealed record VehicleSourceRemoval
{
    public required string Code { get; init; }

    public int ListingsDeleted { get; init; }

    /// <summary>Vehicles removed because this source was the last one offering them.</summary>
    public int VehiclesDeleted { get; init; }

    /// <summary>
    /// Vehicles that survived because another source still lists them.
    /// </summary>
    /// <remarks>
    /// Reported rather than left to be inferred from a count that does not add up. Deleting a
    /// source that shared stock with another should visibly remove less than it held.
    /// </remarks>
    public int VehiclesKept { get; init; }

    public int ImagesDeleted { get; init; }

    public int SyncJobsDeleted { get; init; }

    public int TenantOverlaysDeleted { get; init; }
}

/// <summary>
/// Deletes a vehicle source and the data only it was holding up.
/// </summary>
/// <remarks>
/// Runs in a context with no tenant resolved, like sync and import: it deletes global catalog
/// rows, and GuardGlobalCatalogWrites refuses those from a tenant-scoped path.
///
/// The ordering here is not stylistic. VehicleListing, SyncJob, VehicleMatchCandidate and
/// VehicleMergeHistory all reference their targets with DeleteBehavior.Restrict, so each has to
/// be removed before the row it points at or SQL Server rejects the delete. Cascades cover the
/// rest: a vehicle takes its images and tenant overlays with it.
/// </remarks>
public sealed class VehicleSourceRemovalService
{
    private readonly CarDealerDbContext _db;
    private readonly ILogger<VehicleSourceRemovalService> _logger;

    public VehicleSourceRemovalService(
        CarDealerDbContext db, ILogger<VehicleSourceRemovalService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<VehicleSourceRemoval?> RemoveAsync(string code, CancellationToken ct = default)
    {
        var source = await _db.VehicleSources
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Code == code, ct)
            .ConfigureAwait(false);

        if (source is null)
        {
            return null;
        }

        var listingIds = await _db.VehicleListings
            .IgnoreQueryFilters()
            .Where(l => l.VehicleSourceId == source.Id)
            .Select(l => l.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var touchedVehicleIds = await _db.VehicleListings
            .IgnoreQueryFilters()
            .Where(l => l.VehicleSourceId == source.Id)
            .Select(l => l.VehicleId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // A car another source still offers is that source's car too. Deleting BE FORWARD must
        // not silently remove vehicles SBT is also selling, so only those left with no listing
        // at all are removed.
        var orphanedVehicleIds = await _db.VehicleListings
            .IgnoreQueryFilters()
            .Where(l => touchedVehicleIds.Contains(l.VehicleId))
            .GroupBy(l => l.VehicleId)
            .Where(g => g.All(l => l.VehicleSourceId == source.Id))
            .Select(g => g.Key)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var imagesToDelete = await _db.VehicleImages
            .IgnoreQueryFilters()
            .CountAsync(i => orphanedVehicleIds.Contains(i.VehicleId), ct)
            .ConfigureAwait(false);

        var overlaysToDelete = await _db.TenantVehicles
            .IgnoreQueryFilters()
            .CountAsync(o => orphanedVehicleIds.Contains(o.VehicleId), ct)
            .ConfigureAwait(false);

        // 1. Listing images first: they Restrict on VehicleImageId, so the join rows have to go
        //    before the images the vehicle delete will cascade away.
        await _db.VehicleListingImages
            .IgnoreQueryFilters()
            .Where(li => listingIds.Contains(li.VehicleListingId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // 2. This source's listings. VehicleSourceId Restricts, so the source cannot go first.
        var listingsDeleted = await _db.VehicleListings
            .IgnoreQueryFilters()
            .Where(l => l.VehicleSourceId == source.Id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // 3. Match candidates and merge history reference vehicles with Restrict, so any row
        //    naming a vehicle about to be deleted has to go first - from either side of the
        //    pair, since a surviving vehicle may still point at a deleted one.
        if (orphanedVehicleIds.Count > 0)
        {
            await _db.VehicleMatchCandidates
                .IgnoreQueryFilters()
                .Where(c => orphanedVehicleIds.Contains(c.VehicleId)
                    || orphanedVehicleIds.Contains(c.CandidateVehicleId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            await _db.VehicleMergeHistories
                .IgnoreQueryFilters()
                .Where(m => orphanedVehicleIds.Contains(m.SurvivingVehicleId)
                    || orphanedVehicleIds.Contains(m.MergedVehicleId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        // 4. The vehicles nothing lists any more. Images and tenant overlays cascade.
        var vehiclesDeleted = orphanedVehicleIds.Count == 0
            ? 0
            : await _db.Vehicles
                .IgnoreQueryFilters()
                .Where(v => orphanedVehicleIds.Contains(v.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

        // 5. Sync history. SyncJob Restricts on VehicleSourceId; its items cascade.
        var jobsDeleted = await _db.SyncJobs
            .IgnoreQueryFilters()
            .Where(j => j.VehicleSourceId == source.Id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // 6. Finally the source. Configurations and make/model aliases cascade.
        await _db.VehicleSources
            .IgnoreQueryFilters()
            .Where(s => s.Id == source.Id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var kept = touchedVehicleIds.Count - orphanedVehicleIds.Count;

        _logger.LogWarning(
            "Deleted vehicle source {Code}: {Listings} listing(s), {Deleted} vehicle(s) removed, "
            + "{Kept} kept because another source still lists them.",
            code, listingsDeleted, vehiclesDeleted, kept);

        return new VehicleSourceRemoval
        {
            Code = code,
            ListingsDeleted = listingsDeleted,
            VehiclesDeleted = vehiclesDeleted,
            VehiclesKept = kept,
            ImagesDeleted = imagesToDelete,
            SyncJobsDeleted = jobsDeleted,
            TenantOverlaysDeleted = overlaysToDelete,
        };
    }
}
