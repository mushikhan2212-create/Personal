using CarDealer.Application.Abstractions;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Integrations.Carapis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.Sync;

/// <summary>Options for one sync run. Quotas are configuration, never constants in code.</summary>
public sealed record VehicleSyncOptions
{
    public required string SourceCode { get; init; }

    /// <summary>
    /// Hard ceiling on pages. Master prompt section 18 forbids unlimited synchronization
    /// without filters and quotas; this is the quota, and the caller owns it.
    /// </summary>
    public int MaxPages { get; init; } = 5;

    public int PageSize { get; init; } = 100;

    /// <summary>
    /// Fetch each vehicle's detail record as well as its list entry.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately a choice rather than an automatic behaviour: only the
    /// detail response carries <c>vin</c>, <c>listing_id</c> and <c>price_original</c>, so
    /// turning this on multiplies the request count by the page size - a page of 100 becomes
    /// 101 requests. Without it the run is cheap but has no strong identifier and no
    /// trustworthy price; with it the run deduplicates and prices correctly and costs a
    /// hundred times as much. The POC report needs both numbers, so the flag is measured.
    /// </remarks>
    public bool FetchDetail { get; init; }
}

/// <summary>
/// Result of a run, in the terms master prompt section 8 requires sync logs to report:
/// counts, duration, errors and provider status.
/// </summary>
public sealed record VehicleSyncResult
{
    public required long SyncJobId { get; init; }

    public required SyncJobStatus Status { get; init; }

    public int TotalRecords { get; init; }

    public int Created { get; init; }

    public int Updated { get; init; }

    public int Failed { get; init; }

    /// <summary>Records that matched an existing vehicle on an exact strong identifier.</summary>
    public int AutoMerged { get; init; }

    /// <summary>
    /// Records that arrived with no strong identifier at all, so nothing can be merged on.
    /// For Japanese export stock without VINs this is expected to be most of them, and it is
    /// the number that tells the reader how much of the catalog dedup cannot help with.
    /// </summary>
    public int WithoutStrongIdentifier { get; init; }

    public int PagesFetched { get; init; }

    public int RequestCount { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Pulls a bounded sample from a source into the global catalog.
/// </summary>
/// <remarks>
/// "Controlled sample, not a full mirror" is master prompt section 3's phrasing, and the
/// quota is what keeps it true.
///
/// Everything written here is a global catalog row - null TenantId - because these come from
/// a shared source (decision D1). That is only permitted because this runs in a background
/// context with no tenant resolved; the DbContext's write guard refuses global writes from any
/// tenant-scoped path.
/// </remarks>
public sealed class VehicleSyncService
{
    private readonly CarDealerDbContext _db;
    private readonly IVehicleSourceSyncProvider? _provider;
    private readonly IVehicleSourceDetailProvider? _detailProvider;
    private readonly CarapisNormalizer _normalizer;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<VehicleSyncService> _logger;

    /// <summary>
    /// The sync provider is optional.
    /// </summary>
    /// <remarks>
    /// Master prompt section 8 requires Carapis be disablable without breaking the platform,
    /// and a required dependency here would break it in the most annoying way possible: with
    /// no API key configured the provider is not registered, this service could not be
    /// constructed, and the controller that depends on it would fail to resolve - turning
    /// "sync is unavailable" into a 500 on an endpoint that should simply say so.
    /// </remarks>
    public VehicleSyncService(
        CarDealerDbContext db,
        CarapisNormalizer normalizer,
        IDateTimeProvider clock,
        ILogger<VehicleSyncService> logger,
        IVehicleSourceSyncProvider? provider = null,
        IVehicleSourceDetailProvider? detailProvider = null)
    {
        _db = db;
        _provider = provider;
        _normalizer = normalizer;
        _clock = clock;
        _logger = logger;
        _detailProvider = detailProvider;
    }

    /// <summary>True when a provider is registered and a run is possible at all.</summary>
    public bool IsConfigured => _provider is not null;

    public async Task<VehicleSyncResult> RunAsync(VehicleSyncOptions options, CancellationToken ct = default)
    {
        if (_provider is null)
        {
            throw new InvalidOperationException(
                "No vehicle source provider is registered. Configure Carapis:ApiKey to enable "
                + "synchronization - every other part of the platform works without it.");
        }

        var startedAt = _clock.UtcNow;

        var source = await _db.VehicleSources
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Code == options.SourceCode, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No VehicleSource is registered with code '{options.SourceCode}'. "
                + "Register it before syncing - the code must be one proven to return data.");

        var job = new SyncJob
        {
            TenantId = null,
            VehicleSourceId = source.Id,
            JobType = SyncJobType.SampleFetch,
            Status = SyncJobStatus.Running,
            StartedAtUtc = startedAt,
        };

        _db.SyncJobs.Add(job);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var created = 0;
        var updated = 0;
        var failed = 0;
        var autoMerged = 0;
        var withoutIdentifier = 0;
        var pagesFetched = 0;
        var requestCount = 0;
        string? error = null;

        try
        {
            for (var pageNumber = 1; pageNumber <= options.MaxPages; pageNumber++)
            {
                var page = await _provider.FetchPageAsync(
                    new VehicleSourceQuery
                    {
                        SourceCode = options.SourceCode,
                        Page = pageNumber,
                        PageSize = options.PageSize,
                    },
                    ct).ConfigureAwait(false);

                pagesFetched++;
                requestCount++;

                foreach (var listRecord in page.Records)
                {
                    var record = listRecord;

                    if (options.FetchDetail && _detailProvider is not null)
                    {
                        // The expensive path. Only here does vin, listing_id and
                        // price_original become available.
                        var detail = await _detailProvider
                            .FetchDetailAsync(listRecord.ExternalId, ct)
                            .ConfigureAwait(false);

                        requestCount++;

                        if (detail is not null)
                        {
                            record = detail;
                        }
                    }

                    try
                    {
                        var outcome = await UpsertAsync(record, source.Id, job.Id, ct).ConfigureAwait(false);

                        switch (outcome)
                        {
                            case UpsertOutcome.Created: created++; break;
                            case UpsertOutcome.Updated: updated++; break;
                            case UpsertOutcome.MergedIntoExisting: autoMerged++; break;
                            case UpsertOutcome.Skipped: failed++; break;
                        }

                        if (outcome is UpsertOutcome.Created or UpsertOutcome.Updated)
                        {
                            // Counted separately from the outcome: a record can be created and
                            // still carry no identifier, and that is the number worth knowing.
                            withoutIdentifier += await HasNoStrongIdentifierAsync(record, source.Id) ? 1 : 0;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;

                        _db.SyncJobItems.Add(new SyncJobItem
                        {
                            SyncJobId = job.Id,
                            ExternalListingId = record.ExternalId,
                            Status = SyncJobItemStatus.Failed,
                            ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message,
                            ProcessedAtUtc = _clock.UtcNow,
                        });

                        // One bad record does not abandon the run. The item row records which.
                        _logger.LogWarning(ex,
                            "Sync item {ExternalId} from {Source} failed.", record.ExternalId, options.SourceCode);
                    }
                }

                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (!page.HasNextPage)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            _logger.LogError(ex, "Sync run for {Source} failed.", options.SourceCode);
        }

        var completedAt = _clock.UtcNow;

        job.Status = error is not null
            ? SyncJobStatus.Failed
            : failed > 0
                ? SyncJobStatus.PartiallySucceeded
                : SyncJobStatus.Succeeded;

        job.CompletedAtUtc = completedAt;
        job.TotalRecords = created + updated + autoMerged + failed;
        job.CreatedRecords = created;
        job.UpdatedRecords = updated;
        job.FailedRecords = failed;
        job.ErrorMessage = error;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Sync {Source}: {Created} created, {Updated} updated, {Merged} merged, {Failed} failed "
            + "across {Pages} page(s) and {Requests} request(s) in {Elapsed}.",
            options.SourceCode, created, updated, autoMerged, failed,
            pagesFetched, requestCount, completedAt - startedAt);

        return new VehicleSyncResult
        {
            SyncJobId = job.Id,
            Status = job.Status,
            TotalRecords = job.TotalRecords,
            Created = created,
            Updated = updated,
            Failed = failed,
            AutoMerged = autoMerged,
            WithoutStrongIdentifier = withoutIdentifier,
            PagesFetched = pagesFetched,
            RequestCount = requestCount,
            Elapsed = completedAt - startedAt,
            ErrorMessage = error,
        };
    }

    private enum UpsertOutcome { Created, Updated, MergedIntoExisting, Skipped }

    private async Task<UpsertOutcome> UpsertAsync(
        RawVehicleRecord record, long sourceId, long syncJobId, CancellationToken ct)
    {
        var normalized = _normalizer.Normalize(record, sourceId);

        if (normalized is null)
        {
            _db.SyncJobItems.Add(new SyncJobItem
            {
                SyncJobId = syncJobId,
                ExternalListingId = record.ExternalId,
                Status = SyncJobItemStatus.Skipped,
                ErrorMessage = "Payload carried no usable identifier.",
                ProcessedAtUtc = _clock.UtcNow,
            });

            return UpsertOutcome.Skipped;
        }

        var externalId = normalized.Listing.ExternalListingId;

        // Re-ingesting a listing we already hold. IgnoreQueryFilters because this runs with no
        // tenant resolved, and the global rows would otherwise be invisible to their own writer.
        var existingListing = await _db.VehicleListings
            .IgnoreQueryFilters()
            .Include(l => l.Vehicle)
            .FirstOrDefaultAsync(
                l => l.VehicleSourceId == sourceId && l.ExternalListingId == externalId, ct)
            .ConfigureAwait(false);

        if (existingListing is not null)
        {
            existingListing.Price = normalized.Listing.Price;
            existingListing.CurrencyCode = normalized.Listing.CurrencyCode;
            existingListing.LastSeenAtUtc = normalized.Listing.LastSeenAtUtc;
            existingListing.LastSyncedAtUtc = normalized.Listing.LastSyncedAtUtc;
            existingListing.IsActive = normalized.Listing.IsActive;
            existingListing.RawPayload = normalized.Listing.RawPayload;
            existingListing.Vehicle.Status = normalized.Vehicle.Status;

            _db.SyncJobItems.Add(Item(syncJobId, externalId, SyncJobItemStatus.Updated));
            return UpsertOutcome.Updated;
        }

        // Decision D3: auto-merge only on exact equality of a NON-NULL canonical hash. A null
        // hash never matches anything, including another null - which is what keeps VIN-less
        // vehicles distinct rather than collapsing them into one.
        Vehicle? survivor = null;

        if (normalized.Vehicle.CanonicalHash is { } hash)
        {
            // The change tracker first, then the database. Both halves are needed: a vehicle
            // added earlier in this same page has not been saved yet, so a database-only
            // lookup would miss it and create a second row - making deduplication depend on
            // where the page boundary happened to fall, which is not a property anyone could
            // reason about. Caught by Two_listings_sharing_a_vin_attach_to_one_vehicle.
            survivor = _db.Vehicles.Local.FirstOrDefault(v => v.CanonicalHash == hash)
                ?? await _db.Vehicles
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(v => v.CanonicalHash == hash, ct)
                    .ConfigureAwait(false);
        }

        if (survivor is not null)
        {
            // Same physical car, newly offered by this source. The listing attaches to the
            // vehicle we already have rather than creating a second one.
            //
            // By reference rather than by id: a survivor found in the change tracker has not
            // been saved, so its Id is still 0, and assigning that would orphan the listing.
            normalized.Listing.Vehicle = survivor;
            _db.VehicleListings.Add(normalized.Listing);

            _db.VehicleMergeHistories.Add(new VehicleMergeHistory
            {
                SurvivingVehicle = survivor,
                MergedVehicle = survivor,
                MergedByUserId = null, // null means an automatic strong-identifier merge
                ReasonsJson =
                    $$"""{"rule":"{{normalized.Vehicle.CanonicalHashSource}}","hash":"{{normalized.Vehicle.CanonicalHash}}","listing":"{{externalId}}"}""",
                MergedAtUtc = _clock.UtcNow,
            });

            _db.SyncJobItems.Add(Item(syncJobId, externalId, SyncJobItemStatus.Updated));
            return UpsertOutcome.MergedIntoExisting;
        }

        normalized.Listing.Vehicle = normalized.Vehicle;
        _db.Vehicles.Add(normalized.Vehicle);
        _db.VehicleListings.Add(normalized.Listing);

        foreach (var image in normalized.Images)
        {
            image.Vehicle = normalized.Vehicle;
            _db.VehicleImages.Add(image);
        }

        _db.SyncJobItems.Add(Item(syncJobId, externalId, SyncJobItemStatus.Created));
        return UpsertOutcome.Created;
    }

    private Task<bool> HasNoStrongIdentifierAsync(RawVehicleRecord record, long sourceId)
    {
        var normalized = _normalizer.Normalize(record, sourceId);
        return Task.FromResult(normalized?.Vehicle.CanonicalHash is null);
    }

    private SyncJobItem Item(long syncJobId, string? externalId, SyncJobItemStatus status)
        => new()
        {
            SyncJobId = syncJobId,
            ExternalListingId = externalId,
            Status = status,
            ProcessedAtUtc = _clock.UtcNow,
        };
}
