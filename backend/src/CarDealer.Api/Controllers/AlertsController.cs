using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Abstractions;
using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Alerts;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Api.Controllers;

/// <summary>
/// Cars that arrived after a customer asked for them.
/// </summary>
/// <remarks>
/// The inbox for <see href="../../../docs/spec/05-open-items.md">O11</see>. An alert names a
/// customer's requirement, so it is exactly as private as the customer is: every query here
/// runs through the DbContext's tenant filter and none of them calls <c>IgnoreQueryFilters</c>.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/alerts")]
public sealed class AlertsController : ControllerBase
{
    private readonly CarDealerDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IDateTimeProvider _clock;

    public AlertsController(
        CarDealerDbContext db, ICurrentUser currentUser, IDateTimeProvider clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    /// <summary>How many alerts nobody has looked at. Drives the count on the bell.</summary>
    [HttpGet("count")]
    [HasPermission(Permissions.CustomersRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Count(CancellationToken ct)
    {
        var unseen = await _db.RequirementAlerts
            .CountAsync(a => a.SeenAtUtc == null, ct)
            .ConfigureAwait(false);

        return Ok(new { unseen });
    }

    /// <summary>
    /// The alerts, newest first, with the customer and car each is about.
    /// </summary>
    /// <remarks>
    /// Everything needed to decide whether to pick up the phone is on the row - who wants it,
    /// what they asked for, which car, what it costs. A list that only said "3 new matches"
    /// would make every alert a navigation exercise.
    /// </remarks>
    [HttpGet]
    [HasPermission(Permissions.CustomersRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] bool unseenOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var alerts = _db.RequirementAlerts.AsNoTracking();

        if (unseenOnly)
        {
            alerts = alerts.Where(a => a.SeenAtUtc == null);
        }

        var total = await alerts.CountAsync(ct).ConfigureAwait(false);
        var size = Math.Clamp(pageSize, 1, 100);

        var items = await alerts
            // Unseen first, then newest: the ones needing action stay at the top even after a
            // busy week, rather than being pushed under alerts somebody already dealt with.
            .OrderBy(a => a.SeenAtUtc == null ? 0 : 1)
            .ThenByDescending(a => a.MatchedAtUtc)
            .Skip((Math.Max(1, page) - 1) * size)
            .Take(size)
            .Select(a => new
            {
                // The external identifier, so the inbox posts back a GUID rather than a
                // sequential key (D17).
                Id = a.PublicId,
                a.MatchedAtUtc,
                a.SeenAtUtc,
                a.PriceBaseAtMatch,
                a.BaseCurrencyCode,

                Customer = new
                {
                    a.CustomerRequirement.Customer.PublicId,
                    a.CustomerRequirement.Customer.FirstName,
                    a.CustomerRequirement.Customer.LastName,
                    a.CustomerRequirement.Customer.Phone,
                },

                Requirement = new
                {
                    a.CustomerRequirementId,
                    a.CustomerRequirement.Name,
                    a.CustomerRequirement.Make,
                    a.CustomerRequirement.Model,
                },

                Vehicle = new
                {
                    a.Vehicle.PublicId,
                    a.Vehicle.Make,
                    a.Vehicle.Model,
                    a.Vehicle.Variant,
                    Year = a.Vehicle.ModelYear,
                    a.Vehicle.Mileage,
                    MileageUnit = a.Vehicle.MileageUnit.ToString(),
                    ImageUrl = a.Vehicle.Images
                        .OrderBy(i => i.SortOrder)
                        .Select(i => i.ImageUrl)
                        .FirstOrDefault(),
                },
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(new { items, totalCount = total, page = Math.Max(1, page), pageSize = size });
    }

    /// <summary>Marks one alert as seen.</summary>
    /// <remarks>
    /// Needs <c>customers.manage</c> rather than read: in a shared inbox, marking an alert seen
    /// tells colleagues it has been dealt with, and someone with read-only access clearing
    /// somebody else's queue is not a read.
    /// </remarks>
    [HttpPost("{publicId:guid}/seen")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkSeen(Guid publicId, CancellationToken ct)
    {
        var alert = await _db.RequirementAlerts
            .FirstOrDefaultAsync(a => a.PublicId == publicId, ct)
            .ConfigureAwait(false);

        if (alert is null)
        {
            // 404 rather than 403 for another tenant's alert: confirming the id exists would
            // leak that somebody else has a customer waiting for a particular car.
            return NotFound(new ProblemDetails
            {
                Title = $"No alert with id '{publicId}'.",
                Status = StatusCodes.Status404NotFound,
            });
        }

        Stamp(alert);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { id = alert.PublicId, alert.SeenAtUtc });
    }

    /// <summary>Marks every unseen alert as seen.</summary>
    [HttpPost("seen")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllSeen(CancellationToken ct)
    {
        var unseen = await _db.RequirementAlerts
            .Where(a => a.SeenAtUtc == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var alert in unseen)
        {
            Stamp(alert);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { marked = unseen.Count });
    }

    /// <summary>
    /// Runs the scan for this tenant now, rather than waiting for the schedule.
    /// </summary>
    /// <remarks>
    /// Here because the alternative is waiting an hour to find out whether an import produced
    /// the alerts it should have. Idempotent by construction - the unique index means a second
    /// run over the same stock raises nothing - so triggering it twice costs a query, not a
    /// duplicate inbox.
    /// </remarks>
    [HttpPost("scan")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Scan(
        [FromServices] RequirementAlertScanner scanner, CancellationToken ct)
    {
        var result = await scanner.ScanAsync(ct).ConfigureAwait(false);

        return Ok(new
        {
            requirementsScanned = result.RequirementsScanned,
            alertsRaised = result.AlertsRaised,
        });
    }

    private void Stamp(RequirementAlert alert)
    {
        // Left alone if it is already seen, so the record keeps who cleared it first rather
        // than whoever most recently opened the screen.
        if (alert.SeenAtUtc is not null) return;

        alert.SeenAtUtc = _clock.UtcNow;
        alert.SeenByUserId = _currentUser.UserId;
    }
}
