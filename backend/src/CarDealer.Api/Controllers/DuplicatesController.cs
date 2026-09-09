using System.Text.Json;
using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Abstractions;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Duplicates;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.Api.Controllers;

/// <summary>
/// Suggested duplicate vehicles, and a person's decision about them.
/// </summary>
/// <remarks>
/// The review queue for <see href="../../../docs/spec/05-open-items.md">O15</see>. Nothing here
/// merges automatically: the scan writes suggestions, this endpoint shows them with the reasons
/// attached, and a merge happens only when somebody with <c>vehicles.merge</c> says so.
///
/// <para>
/// <c>IgnoreQueryFilters</c> appears throughout, and it is deliberate. Most of the catalogue is
/// the global rows decision D1 shares between tenants, and those rows have a null
/// <c>TenantId</c>, so the ordinary filter would hide exactly the vehicles this queue is about.
/// Ownership is enforced where it matters instead - the scan only ever pairs vehicles with the
/// same <c>TenantScope</c>, and <see cref="VehicleMergeService"/> checks it again before
/// writing.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/duplicates")]
public sealed class DuplicatesController : ControllerBase
{
    private const int MaxPageSize = 100;

    private readonly CarDealerDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurrentUser _currentUser;

    public DuplicatesController(
        CarDealerDbContext db,
        IServiceScopeFactory scopeFactory,
        ICurrentUser currentUser)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _currentUser = currentUser;
    }

    /// <summary>
    /// A unit of work with no tenant resolved, for the writes that touch the shared catalogue.
    /// </summary>
    /// <remarks>
    /// Most of this catalogue is the global rows decision D1 shares, and
    /// <c>GuardGlobalCatalogWrites</c> refuses those from a tenant-scoped request path - a read
    /// filter is not a write guard, and without it any tenant could edit a shared car for
    /// everyone. Merging is a deliberate administrative act on exactly those rows, so it runs
    /// the same way source registration and import already do: authorization runs against the
    /// caller first, and only then does the write happen in a scope that is nobody's tenant.
    /// </remarks>
    private T Unscoped<T>(IServiceScope scope) where T : notnull
        => scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>How many pairs are waiting for a decision.</summary>
    [HttpGet("count")]
    [HasPermission(Permissions.VehiclesMerge)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Count(CancellationToken ct)
    {
        var pending = await LiveQueue()
            .CountAsync(ct)
            .ConfigureAwait(false);

        return Ok(new { pending });
    }

    /// <summary>
    /// The review queue, strongest suggestion first.
    /// </summary>
    /// <remarks>
    /// Highest score first because that is the order in which a reviewer's attention is worth
    /// most: the top of the queue is where the obvious duplicates are, and somebody who works
    /// down it until the suggestions stop being obvious has done the useful part of the job.
    /// </remarks>
    [HttpGet]
    [HasPermission(Permissions.VehiclesMerge)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] MatchCandidateStatus status = MatchCandidateStatus.Pending,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = status == MatchCandidateStatus.Pending
            ? LiveQueue()
            : _db.VehicleMatchCandidates.AsNoTracking().Where(c => c.Status == status);

        var total = await query.CountAsync(ct).ConfigureAwait(false);

        var candidates = await query
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var vehicleIds = candidates
            .SelectMany(c => new[] { c.VehicleId, c.CandidateVehicleId })
            .Distinct()
            .ToList();

        var vehicles = await _db.Vehicles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => vehicleIds.Contains(v.Id))
            .Select(v => new
            {
                v.Id,
                v.PublicId,
                v.Make,
                v.Model,
                v.Variant,
                v.ModelYear,
                v.Mileage,
                v.MileageUnit,
                v.ExteriorColor,
                v.EngineDisplacementCc,
                v.FuelType,
                v.Transmission,
                v.SteeringSide,
                v.Status,
                v.CreatedAtUtc,

                // The offers are what makes a merge worth doing: two rows for one car means the
                // cheaper price is on a screen nobody is looking at. Shown per side so the
                // reviewer sees exactly what would end up on the survivor.
                Offers = v.Listings.Select(l => new
                {
                    l.Id,
                    Source = l.VehicleSource.Name,
                    l.Price,
                    l.CurrencyCode,
                    l.PriceType,
                    l.SourceUrl,
                }).ToList(),

                Photo = v.Images.OrderBy(i => i.SortOrder).Select(i => i.ImageUrl).FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byId = vehicles.ToDictionary(v => v.Id);

        var items = candidates
            .Where(c => byId.ContainsKey(c.VehicleId) && byId.ContainsKey(c.CandidateVehicleId))
            .Select(c => new
            {
                id = c.Id,
                score = c.Score,
                status = c.Status.ToString(),
                createdAtUtc = c.CreatedAtUtc,
                reviewedAtUtc = c.ReviewedAtUtc,

                // Parsed rather than passed through as a string, so the screen renders the
                // reasons as a list instead of printing raw JSON at somebody.
                signals = Signals(c.SignalsJson),

                left = byId[c.VehicleId],
                right = byId[c.CandidateVehicleId],
            });

        return Ok(new { totalCount = total, page, pageSize, items });
    }

    /// <summary>
    /// The pairs still genuinely waiting on somebody, which is what both the queue and its
    /// count must mean.
    /// </summary>
    /// <remarks>
    /// A pair whose car has already been archived is not a live question any more. Three
    /// vehicles sharing a blocking key produce three pairs - (A,B), (A,C), (B,C) - and merging
    /// the first archives B, which leaves (B,C) asking whether an archived row is the same car
    /// as C. (A,C) still covers that comparison properly, so the stale pair is hidden rather
    /// than answered: it keeps its Pending row, and reversing the merge brings B back and the
    /// question with it.
    ///
    /// <para>
    /// Shared with <c>count</c> deliberately. A badge that says twelve over a list showing ten
    /// sends somebody looking for two suggestions that were never there.
    /// </para>
    /// </remarks>
    private IQueryable<VehicleMatchCandidate> LiveQueue()
    {
        var archived = _db.Vehicles
            .IgnoreQueryFilters()
            .Where(v => v.Status == VehicleStatus.Archived)
            .Select(v => v.Id);

        return _db.VehicleMatchCandidates
            .AsNoTracking()
            .Where(c => c.Status == MatchCandidateStatus.Pending
                && !archived.Contains(c.VehicleId)
                && !archived.Contains(c.CandidateVehicleId));
    }

    /// <summary>
    /// Confirms that two rows are one car, and merges them.
    /// </summary>
    /// <remarks>
    /// The duplicate's listings and photos move onto the survivor and the duplicate is archived,
    /// never deleted - which is what lets <c>revert</c> put it back. The surviving vehicle keeps
    /// the older of the two ids, because other records already point at it.
    /// </remarks>
    [HttpPost("{id:long}/merge")]
    [HasPermission(Permissions.VehiclesMerge)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Merge(
        long id, [FromBody] MergeDecisionRequest? request, CancellationToken ct)
    {
        // A merge is attributed to whoever approved it - that attribution is half of what makes
        // the record worth keeping - so an unidentifiable caller is refused rather than written
        // as an anonymous merge.
        if (_currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        using var scope = _scopeFactory.CreateScope();

        var outcome = await Unscoped<VehicleMergeService>(scope)
            .MergeAsync(id, userId, request?.Note, ct)
            .ConfigureAwait(false);

        return outcome.Status switch
        {
            MergeStatus.Merged => Ok(new
            {
                survivingVehicleId = outcome.SurvivingPublicId,
                archivedVehicleId = outcome.ArchivedPublicId,
                listingsMoved = outcome.ListingsMoved,
                imagesMoved = outcome.ImagesMoved,
            }),

            MergeStatus.NotFound => NotFound(new ProblemDetails
            {
                Title = $"No pending duplicate candidate with id {id}.",
                Status = StatusCodes.Status404NotFound,
            }),

            MergeStatus.AlreadyReviewed => Conflict(new ProblemDetails
            {
                Title = "Somebody has already decided this one.",
                Detail = "Reload the queue to see the current state.",
                Status = StatusCodes.Status409Conflict,
            }),

            // Should be unreachable: the scan groups on TenantScope so it never pairs across
            // owners. Answered rather than thrown because the alternative to a clear refusal
            // here is the cross-tenant merge decision D1 exists to prevent.
            MergeStatus.DifferentOwners => Conflict(new ProblemDetails
            {
                Title = "These vehicles belong to different owners and cannot be merged.",
                Status = StatusCodes.Status409Conflict,
            }),

            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>Records that the two rows really are different cars.</summary>
    [HttpPost("{id:long}/reject")]
    [HasPermission(Permissions.VehiclesMerge)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reject(long id, CancellationToken ct)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        using var scope = _scopeFactory.CreateScope();

        var rejected = await Unscoped<VehicleMergeService>(scope)
            .RejectAsync(id, userId, ct)
            .ConfigureAwait(false);

        return rejected
            ? NoContent()
            : NotFound(new ProblemDetails
            {
                Title = $"No pending duplicate candidate with id {id}.",
                Status = StatusCodes.Status404NotFound,
            });
    }

    /// <summary>The merges that have been carried out, newest first.</summary>
    [HttpGet("merges")]
    [HasPermission(Permissions.VehiclesMerge)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Merges(
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        // Only the reviewed merges. The automatic strong-identifier path also writes history,
        // with both ids equal, and those are not undoable - listing them here would offer a
        // button that cannot work.
        var merges = await _db.VehicleMergeHistories
            .AsNoTracking()
            .Where(h => h.SurvivingVehicleId != h.MergedVehicleId)
            .OrderByDescending(h => h.MergedAtUtc)
            .Take(pageSize)
            .Select(h => new
            {
                id = h.Id,
                mergedAtUtc = h.MergedAtUtc,
                revertedAtUtc = h.RevertedAtUtc,
                mergedBy = h.MergedByUser == null ? null : h.MergedByUser.Email,
                surviving = _db.Vehicles.IgnoreQueryFilters()
                    .Where(v => v.Id == h.SurvivingVehicleId)
                    .Select(v => new { v.PublicId, v.Make, v.Model, v.ModelYear })
                    .FirstOrDefault(),
                archived = _db.Vehicles.IgnoreQueryFilters()
                    .Where(v => v.Id == h.MergedVehicleId)
                    .Select(v => new { v.PublicId, v.Make, v.Model, v.ModelYear })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(new { items = merges });
    }

    /// <summary>Undoes a merge, putting the archived vehicle and its listings back.</summary>
    [HttpPost("merges/{id:long}/revert")]
    [HasPermission(Permissions.VehiclesMerge)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revert(long id, CancellationToken ct)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        using var scope = _scopeFactory.CreateScope();

        var reverted = await Unscoped<VehicleMergeService>(scope)
            .RevertAsync(id, userId, ct)
            .ConfigureAwait(false);

        return reverted
            ? NoContent()
            : NotFound(new ProblemDetails
            {
                Title = $"No reversible merge with id {id}.",
                Status = StatusCodes.Status404NotFound,
            });
    }

    /// <summary>
    /// Runs the scan now rather than waiting for the nightly job.
    /// </summary>
    /// <remarks>
    /// Here so that importing a file and seeing what it duplicated is one sitting rather than
    /// two. Idempotent: a pair already in the queue, or already ruled on, is not raised again.
    /// </remarks>
    [HttpPost("scan")]
    [HasPermission(Permissions.VehiclesMerge)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Scan(CancellationToken ct)
    {
        // Same reason: the scan writes candidate rows about global vehicles.
        using var scope = _scopeFactory.CreateScope();

        var result = await Unscoped<DuplicateScanService>(scope)
            .ScanAsync(ct)
            .ConfigureAwait(false);

        return Ok(new
        {
            pairsExamined = result.PairsExamined,
            candidatesRaised = result.CandidatesRaised,
            groupsSkipped = result.GroupsSkipped,
        });
    }

    private static object[] Signals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return [.. document.RootElement.EnumerateArray().Select(e => new
            {
                name = e.TryGetProperty("Name", out var n) ? n.GetString() : null,
                weight = e.TryGetProperty("Weight", out var w) ? w.GetDecimal() : 0m,
                detail = e.TryGetProperty("Detail", out var d) ? d.GetString() : null,
            })];
        }
        catch (JsonException)
        {
            // A candidate whose reasons cannot be read is still a candidate worth reviewing -
            // the two vehicles are shown side by side either way. Better a row with no
            // explanation than a queue that 500s over one malformed column.
            return [];
        }
    }
}

public sealed record MergeDecisionRequest
{
    /// <summary>Why the reviewer decided this, kept on the merge record.</summary>
    public string? Note { get; init; }
}
