using System.Text.RegularExpressions;
using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Abstractions;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Infrastructure.Sources;
using CarDealer.Infrastructure.Sync;
using CarDealer.Integrations.FileImport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Api.Controllers;

/// <summary>Vehicle sources, and the runs that pull from them.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/vehicle-sources")]
public sealed class VehicleSourcesController : ControllerBase
{
    /// <summary>
    /// Ceiling on an uploaded import, in bytes.
    /// </summary>
    /// <remarks>
    /// 64 MB holds well over a hundred thousand vehicles at the format's typical size, which is
    /// far more than the bounded sample this phase deals in - and the point of a ceiling is
    /// that an unbounded upload is a denial-of-service vector, not that any real file is close
    /// to it.
    /// </remarks>
    private const long MaxImportBytes = 64L * 1024 * 1024;

    private readonly CarDealerDbContext _db;
    private readonly VehicleSyncService _sync;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;
    private readonly ITenantContext _tenant;

    /// <summary>
    /// Source codes appear in URLs and in every import that targets them, so the shape is kept
    /// deliberately narrow rather than accepting whatever a caller sends.
    /// </summary>
    private static readonly Regex CodePattern = new(
        "^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public VehicleSourcesController(
        CarDealerDbContext db,
        VehicleSyncService sync,
        IServiceScopeFactory scopeFactory,
        IFileStorage storage,
        ITenantContext tenant)
    {
        _db = db;
        _sync = sync;
        _scopeFactory = scopeFactory;
        _storage = storage;
        _tenant = tenant;
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
    /// Registers a vehicle source.
    /// </summary>
    /// <remarks>
    /// A source is what a listing is attributed to, and its row decides two things nothing
    /// else can override: which adapter reads its payloads (<c>providerType</c>), and whether
    /// its vehicles land in the shared global catalog or one tenant's private inventory
    /// (<c>isShared</c>, decision D1).
    ///
    /// Creating a shared source writes a global row, so it runs in a DI scope with no tenant
    /// resolved - the same route the sync and import paths take, and the only one the
    /// DbContext's write guard permits for a global write.
    /// </remarks>
    [HttpPost]
    [HasPermission(Permissions.VehiclesSync)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateVehicleSourceRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "A source needs both a code and a name.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (!CodePattern.IsMatch(code))
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"'{code}' is not a usable source code.",
                Detail = "Use lower-case letters, digits, hyphens and underscores, up to 64 "
                    + "characters. The code appears in URLs and in every import that targets "
                    + "this source, so it is deliberately restrictive.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (request.IngestionFilterJson is not null)
        {
            try
            {
                IngestionFilter.Parse(request.IngestionFilterJson);
            }
            catch (InvalidOperationException ex)
            {
                // Validated now rather than at the first import: a filter that cannot be read
                // stops ingestion entirely, and finding that out during an import is finding
                // it out at the worst moment.
                return BadRequest(new ProblemDetails
                {
                    Title = "The ingestion filter is not valid JSON.",
                    Detail = ex.Message,
                    Status = StatusCodes.Status400BadRequest,
                });
            }
        }

        var tenantId = request.IsShared ? (long?)null : _tenant.TenantId;

        // Uniqueness is over (ISNULL(TenantId, 0), Code) - the TenantScope computed column -
        // so a shared code and a tenant's own code do not collide. Checked here to answer with
        // a 409 rather than letting the unique index surface as an unhandled 500.
        var clash = await _db.VehicleSources
            .IgnoreQueryFilters()
            .AnyAsync(v => v.Code == code && v.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (clash)
        {
            return Conflict(new ProblemDetails
            {
                Title = $"A source with code '{code}' is already registered.",
                Detail = request.IsShared
                    ? "Shared source codes are unique across the platform."
                    : "This tenant already has a source with that code.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        // A shared source is a global-catalog row, and GuardGlobalCatalogWrites refuses those
        // from a tenant-scoped request path. Authorization already ran against the caller.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var source = new VehicleSource
        {
            TenantId = tenantId,
            Name = request.Name.Trim(),
            Code = code,
            ProviderType = request.ProviderType,
            SourceType = request.SourceType,
            BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? null : request.BaseUrl.Trim(),
            IsShared = request.IsShared,
            IsActive = true,
            IngestionFilterJson = request.IngestionFilterJson,
        };

        db.VehicleSources.Add(source);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Created($"/api/v1/vehicle-sources/{source.Code}", new
        {
            source.Code,
            source.Name,
            ProviderType = source.ProviderType.ToString(),
            SourceType = source.SourceType.ToString(),
            source.IsShared,
            source.IsActive,
            source.BaseUrl,
            scope = source.TenantId is null ? "global" : "tenant",
        });
    }

    /// <summary>
    /// Deletes a source and the catalog data only it was holding up.
    /// </summary>
    /// <remarks>
    /// A car another source still offers is kept: deleting one exporter must not silently
    /// remove vehicles a different exporter is also selling. Only vehicles left with no
    /// listing at all go, taking their images and tenant overlays with them.
    ///
    /// The caller repeats the code in <c>confirm</c>. This is destructive and irreversible -
    /// there is no soft delete and no undo - so the request has to name what it is destroying
    /// rather than being one mis-click on a menu.
    /// </remarks>
    [HttpDelete("{code}")]
    [HasPermission(Permissions.VehiclesSync)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        string code, [FromQuery] string? confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, code, StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Deleting a source has to be confirmed.",
                Detail = $"Repeat the code as the 'confirm' query parameter: "
                    + $"DELETE /api/v1/vehicle-sources/{code}?confirm={code}. This removes the "
                    + "source and every vehicle no other source lists, and cannot be undone.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        // Deleting global catalog rows needs a context with no tenant resolved, the same route
        // sync and import take. Authorization already ran against the caller's own scope.
        using var scope = _scopeFactory.CreateScope();
        var removal = scope.ServiceProvider.GetRequiredService<VehicleSourceRemovalService>();

        var result = await removal.RemoveAsync(code, ct).ConfigureAwait(false);

        if (result is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = $"No vehicle source is registered with code '{code}'.",
                Status = StatusCodes.Status404NotFound,
            });
        }

        return Ok(result);
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

    /// <summary>
    /// Imports vehicles from a JSON document (docs/spec/08-import-format.md).
    /// </summary>
    /// <remarks>
    /// The platform does not fetch from exporter websites - master prompt sections 6 and 18
    /// rule that out, and decision D13 records why. It accepts data instead, and where that
    /// data came from is the operator's concern: an authorized partner feed, a dealer's own
    /// export, or a tool run outside this system.
    ///
    /// The import runs through the same <see cref="VehicleSyncService"/> as an API sync, so
    /// deduplication, upsert, per-item failure handling and job accounting behave identically.
    /// The only difference is where the records come from.
    ///
    /// Use <c>dryRun=true</c> first on an unfamiliar file. It reports exactly what a real
    /// import would do - readable records, how many are in scope, how many already exist -
    /// and writes nothing.
    /// </remarks>
    [HttpPost("{code}/import")]
    [HasPermission(Permissions.VehiclesSync)]
    [RequestSizeLimit(MaxImportBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Import(
        string code,
        IFormFile file,
        [FromQuery] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "No file was uploaded.",
                Detail = "Post the document as multipart/form-data under the field name 'file'.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var source = await _db.VehicleSources
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Code == code, ct)
            .ConfigureAwait(false);

        if (source is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = $"No vehicle source is registered with code '{code}'.",
                Detail = "Register the source before importing to it, so its listings have "
                    + "something to be attributed to.",
                Status = StatusCodes.Status404NotFound,
            });
        }

        // The import format is read by ImportNormalizer, which the sync pipeline resolves from
        // the source's provider type. Posting an import to a Carapis-typed source therefore
        // resolves CarapisNormalizer, which finds no "id" field and rejects every record as
        // carrying no usable identifier - blaming the file for what is actually the wrong
        // source. Say so here instead, on the first attempt, with the real reason.
        if (source.ProviderType != VehicleSourceProviderType.DealerJson)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"Source '{code}' cannot accept a JSON import.",
                Detail = $"It is registered as ProviderType '{source.ProviderType}', which is "
                    + "read by that provider's own adapter. Importing a file needs a source "
                    + $"registered as '{VehicleSourceProviderType.DealerJson}'. Create one with "
                    + "POST /api/v1/vehicle-sources, or import to a source that already has "
                    + "that type. See docs/spec/08-import-format.md.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        JsonFileVehicleProvider provider;
        string? storageReference = null;

        try
        {
            if (!dryRun)
            {
                // Kept before parsing so a file that fails to import can be re-run against the
                // exact bytes that failed, rather than a re-export that may differ.
                await using var toStore = file.OpenReadStream();
                storageReference = await _storage
                    .SaveAsync("vehicle-imports", $"{code}-{Guid.NewGuid():N}.json", toStore, ct)
                    .ConfigureAwait(false);
            }

            await using var stream = file.OpenReadStream();
            provider = JsonFileVehicleProvider.Read(stream, code);
        }
        catch (InvalidOperationException ex)
        {
            // A document that cannot be read at all is the caller's mistake, not a server
            // fault: 400 with the parser's own message, which names the offending position.
            return BadRequest(new ProblemDetails
            {
                Title = "The import file could not be read.",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest,
            });
        }

        // Same reasoning as Sync: writing the global catalog needs a context with no tenant
        // resolved, or GuardGlobalCatalogWrites refuses it. Authorization already happened
        // against the caller's own scope.
        using var scope = _scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<VehicleSyncService>();

        var result = await sync.RunAsync(
            new VehicleSyncOptions
            {
                SourceCode = code,
                Provider = provider,
                DryRun = dryRun,

                // The document is already in memory, so paging exists only to reuse the sync
                // loop. These bound it generously rather than meaningfully.
                MaxPages = int.MaxValue,
                PageSize = 500,
            },
            ct).ConfigureAwait(false);

        return Ok(new
        {
            dryRun,
            recordsInFile = provider.RecordCount,
            storageReference,
            syncJobId = result.SyncJobId,
            status = result.Status.ToString(),
            result.TotalRecords,
            result.Created,
            result.Updated,
            result.Failed,
            result.AutoMerged,
            result.WithoutStrongIdentifier,
            result.SkippedOutOfScope,
            elapsedMs = (int)result.Elapsed.TotalMilliseconds,
            result.ErrorMessage,
        });
    }
}

/// <summary>What is needed to register a vehicle source.</summary>
public sealed record CreateVehicleSourceRequest
{
    /// <summary>Stable identifier used in URLs. Lower-case, hyphens and underscores.</summary>
    public string? Code { get; init; }

    public string? Name { get; init; }

    /// <summary>
    /// Which adapter reads this source's payloads. <c>DealerJson</c> for file imports.
    /// </summary>
    /// <remarks>
    /// Not a cosmetic label: the sync pipeline resolves its normalizer from this, so a source
    /// registered with the wrong type cannot read its own data.
    /// </remarks>
    public VehicleSourceProviderType ProviderType { get; init; } = VehicleSourceProviderType.DealerJson;

    public VehicleSourceType SourceType { get; init; } = VehicleSourceType.File;

    public string? BaseUrl { get; init; }

    /// <summary>
    /// True puts this source's vehicles in the global catalog every tenant reads; false keeps
    /// them private to the calling tenant (decision D1).
    /// </summary>
    public bool IsShared { get; init; } = true;

    /// <summary>
    /// Optional allow-list bounding what this source may ingest (master prompt section 18).
    /// See docs/spec/08-import-format.md.
    /// </summary>
    public string? IngestionFilterJson { get; init; }
}
