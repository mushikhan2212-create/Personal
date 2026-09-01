using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Api.Controllers;

/// <summary>Vehicle sources, and the runs that pull from them.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/vehicle-sources")]
public sealed class VehicleSourcesController : ControllerBase
{
    private readonly CarDealerDbContext _db;
    private readonly VehicleSyncService _sync;
    private readonly IServiceScopeFactory _scopeFactory;

    public VehicleSourcesController(
        CarDealerDbContext db, VehicleSyncService sync, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _sync = sync;
        _scopeFactory = scopeFactory;
    }

    /// <summary>Lists the sources this tenant can see: shared ones, plus its own.</summary>
    [HttpGet]
    [HasPermission(Permissions.VehiclesRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var sources = await _db.VehicleSources
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Code,
                s.Name,
                ProviderType = s.ProviderType.ToString(),
                s.IsShared,
                s.IsActive,
                VehicleCount = _db.VehicleListings.Count(l => l.VehicleSourceId == s.Id && l.IsActive),

                // Last run that actually brought data in. A failed run also sets
                // CompletedAtUtc, so counting it here would put a fresh timestamp on a source
                // whose sync had just failed outright - the card would read "last sync two
                // minutes ago" when nothing was synced at all.
                LastSyncAtUtc = _db.SyncJobs
                    .Where(j => j.VehicleSourceId == s.Id
                        && j.CompletedAtUtc != null
                        && (j.Status == SyncJobStatus.Succeeded
                            || j.Status == SyncJobStatus.PartiallySucceeded))
                    .Max(j => (DateTime?)j.CompletedAtUtc),

                // The last attempt, whatever became of it, reported separately so a failure is
                // visible rather than merely absent. A source that has never synced and one
                // whose every sync has failed look identical without this.
                LastAttemptAtUtc = _db.SyncJobs
                    .Where(j => j.VehicleSourceId == s.Id && j.CompletedAtUtc != null)
                    .Max(j => (DateTime?)j.CompletedAtUtc),
                LastAttemptStatus = _db.SyncJobs
                    .Where(j => j.VehicleSourceId == s.Id && j.CompletedAtUtc != null)
                    .OrderByDescending(j => j.CompletedAtUtc)
                    .Select(j => j.Status.ToString())
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(sources);
    }

    /// <summary>
    /// Runs a bounded synchronization against one source.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose for the POC: the response carries the counts, the request count
    /// and the duration, which is exactly what master prompt section 8 asks a sync log to
    /// report and what the POC report is written from. In production this becomes a queued job
    /// - the abstraction for that already exists.
    ///
    /// <c>fetchDetail</c> is the expensive switch. Off, a page costs one request and the
    /// records carry no VIN and no source price. On, each vehicle costs an extra request - a
    /// page of 100 becomes 101 - and deduplication and pricing start working. The response
    /// reports the request count either way so the trade is measured rather than argued about.
    /// </remarks>
    [HttpPost("{code}/sync")]
    [HasPermission(Permissions.VehiclesSync)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Sync(
        string code,
        [FromQuery] int maxPages = 2,
        [FromQuery] int pageSize = 100,
        [FromQuery] bool fetchDetail = false,
        CancellationToken ct = default)
    {
        if (!_sync.IsConfigured)
        {
            // Not an error in the platform - a deliberate configuration. Everything else,
            // including search over already-synced data, keeps working.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "No vehicle source provider is configured.",
                Detail = "Set Carapis:ApiKey to enable synchronization. Search and the rest of "
                    + "the catalog are unaffected.",
                Status = StatusCodes.Status503ServiceUnavailable,
            });
        }

        var exists = await _db.VehicleSources
            .IgnoreQueryFilters()
            .AnyAsync(s => s.Code == code, ct)
            .ConfigureAwait(false);

        if (!exists)
        {
            return NotFound(new ProblemDetails
            {
                Title = $"No vehicle source is registered with code '{code}'.",
                Status = StatusCodes.Status404NotFound,
            });
        }

        // A sync writes the GLOBAL catalog - vehicles with TenantId null, shared by every
        // tenant - and CarDealerDbContext.GuardGlobalCatalogWrites refuses a global write from
        // any context where a tenant is resolved. That guard is right, and this request has a
        // tenant: the caller signed in as one. So the sync runs in its own DI scope, which gets
        // a fresh TenantContext with no tenant set - the "background context where no tenant is
        // resolved" the guard names as the one legitimate route to a global write, and the same
        // context the Hangfire job will run in when this stops being synchronous.
        //
        // Authorization is unaffected: HasPermission has already run against the caller's own
        // scope. Only the data context is unscoped, never the decision to let them in.
        using var scope = _scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<VehicleSyncService>();

        var result = await sync.RunAsync(
            new VehicleSyncOptions
            {
                SourceCode = code,
                MaxPages = Math.Clamp(maxPages, 1, 50),
                PageSize = Math.Clamp(pageSize, 1, 100),
                FetchDetail = fetchDetail,
            },
            ct).ConfigureAwait(false);

        return Ok(new
        {
            syncJobId = result.SyncJobId,
            status = result.Status.ToString(),
            result.TotalRecords,
            result.Created,
            result.Updated,
            result.Failed,
            result.AutoMerged,

            // The number that says how much of this catalog deduplication cannot help with.
            result.WithoutStrongIdentifier,

            result.PagesFetched,
            result.RequestCount,
            elapsedMs = (int)result.Elapsed.TotalMilliseconds,
            result.ErrorMessage,
        });
    }
}
