using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Abstractions;
using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Api.Controllers;

/// <summary>
/// The signed-in user's own choice of which sources feed their searches.
/// </summary>
/// <remarks>
/// Only <c>vehicles.read</c> is required, and that is the point: this is a view preference, not
/// a policy. Switching a source off changes what this person sees and nothing else - not their
/// colleagues, not other tenants, not the catalogue itself - so there is nothing here to
/// protect with a stronger permission. Deciding which sources exist at all is a different job,
/// gated by <c>vehicles.sync</c> on VehicleSourcesController.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/me/sources")]
public sealed class MySourcesController : ControllerBase
{
    private readonly CarDealerDbContext _db;
    private readonly ICurrentUser _currentUser;

    public MySourcesController(CarDealerDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Every source this user can see, and whether it feeds their searches.</summary>
    /// <remarks>
    /// The list is the sources an administrator has registered - shared ones plus this
    /// tenant's own - filtered by the DbContext exactly as everywhere else. A user cannot
    /// enable something that was never registered, and cannot see another tenant's private
    /// sources at all.
    /// </remarks>
    [HttpGet]
    [HasPermission(Permissions.VehiclesRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = _currentUser.UserId;

        // Only the explicit false rows matter; anything absent is enabled.
        var muted = userId is null
            ? []
            : await _db.UserVehicleSourcePreferences
                .Where(p => p.UserId == userId && !p.IsEnabled)
                .Select(p => p.VehicleSourceId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var sources = await _db.VehicleSources
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Code,
                s.Name,
                ProviderType = s.ProviderType.ToString(),
                s.IsShared,
                VehicleCount = _db.VehicleListings.Count(l => l.VehicleSourceId == s.Id && l.IsActive),
                SourceId = s.Id,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(sources.Select(s => new
        {
            s.Code,
            s.Name,
            s.ProviderType,
            s.IsShared,
            s.VehicleCount,
            IsEnabled = !muted.Contains(s.SourceId),
        }));
    }

    /// <summary>Turns one source on or off for this user.</summary>
    [HttpPut("{code}")]
    [HasPermission(Permissions.VehiclesRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Set(
        string code, [FromBody] SetSourcePreferenceRequest request, CancellationToken ct)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        // Resolved through the filtered set, so a user cannot express a preference about a
        // source they are not allowed to know exists.
        var source = await _db.VehicleSources
            .FirstOrDefaultAsync(s => s.Code == code, ct)
            .ConfigureAwait(false);

        if (source is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = $"No vehicle source is registered with code '{code}'.",
                Status = StatusCodes.Status404NotFound,
            });
        }

        var existing = await _db.UserVehicleSourcePreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.VehicleSourceId == source.Id, ct)
            .ConfigureAwait(false);

        if (request.IsEnabled)
        {
            // Enabled is the default, so re-enabling removes the row rather than storing a
            // redundant true. It also means a source keeps behaving correctly for this user if
            // the default ever changes.
            if (existing is not null)
            {
                _db.UserVehicleSourcePreferences.Remove(existing);
            }
        }
        else if (existing is null)
        {
            _db.UserVehicleSourcePreferences.Add(new UserVehicleSourcePreference
            {
                UserId = userId,
                VehicleSourceId = source.Id,
                IsEnabled = false,
            });
        }
        else
        {
            existing.IsEnabled = false;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { code = source.Code, request.IsEnabled });
    }
}

/// <summary>Whether a source should feed the caller's searches.</summary>
public sealed record SetSourcePreferenceRequest
{
    public bool IsEnabled { get; init; }
}
