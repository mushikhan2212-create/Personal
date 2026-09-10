using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Abstractions;
using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Audit;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Api.Controllers;

public sealed record CreateRoleRequest(string Name, string? Description, string[] PermissionCodes);

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/roles")]
public sealed class RolesController : ControllerBase
{
    private readonly CarDealerDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public RolesController(
        CarDealerDbContext db,
        ITenantContext tenantContext,
        ICurrentUser currentUser,
        IAuditService audit)
    {
        _db = db;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _audit = audit;
    }

    /// <summary>Lists system roles plus any roles this tenant has defined.</summary>
    [HttpGet]
    [HasPermission(Permissions.RolesRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // The Role query filter admits TenantId == null (system) or the active tenant.
        var roles = await _db.Roles
            .Select(r => new
            {
                // The external identifier, so DELETE takes back what this handed out (D17).
                Id = r.PublicId,
                r.Name,
                r.Description,
                IsSystemRole = r.TenantId == null,
                Permissions = r.RolePermissions.Select(rp => rp.Permission.Code).OrderBy(c => c).ToList(),
            })
            .OrderBy(r => r.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(roles);
    }

    /// <summary>Creates a role owned by the active tenant.</summary>
    /// <remarks>
    /// Acceptance criterion E4: the name only has to be unique within this tenant, so two
    /// tenants can both define "Sales Manager". The database enforces that through the
    /// unique index on (TenantScope, Name).
    /// </remarks>
    [HttpPost]
    [HasPermission(Permissions.RolesManage)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new ProblemDetails { Title = "Role name is required.", Status = 400 });
        }

        var tenantId = _tenantContext.TenantId;

        var exists = await _db.Roles
            .AnyAsync(r => r.TenantId == tenantId && r.Name == request.Name, ct)
            .ConfigureAwait(false);

        if (exists)
        {
            return Conflict(new ProblemDetails
            {
                Title = "A role with that name already exists in this tenant.",
                Status = 409,
            });
        }

        var permissions = await _db.Permissions
            .Where(p => request.PermissionCodes.Contains(p.Code))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var unknown = request.PermissionCodes.Except(permissions.Select(p => p.Code)).ToArray();

        // Silently dropping unknown codes would create a role that looks like it grants
        // something it does not.
        if (unknown.Length > 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown permission codes.",
                Detail = string.Join(", ", unknown),
                Status = 400,
            });
        }

        var role = new Role
        {
            TenantId = tenantId,
            Name = request.Name.Trim(),
            Description = request.Description,
        };

        foreach (var permission in permissions)
        {
            role.RolePermissions.Add(new RolePermission { Permission = permission });
        }

        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            AuditActions.RoleCreated,
            tenantId,
            _currentUser.UserId,
            nameof(Role),
            role.Id.ToString(),
            new { role.Name, permissions = request.PermissionCodes },
            ct).ConfigureAwait(false);

        return CreatedAtAction(
            nameof(List), new { version = "1.0" }, new { Id = role.PublicId, role.Name });
    }

    /// <summary>Deletes a tenant-defined role.</summary>
    /// <remarks>
    /// Acceptance criterion E5: system roles cannot be deleted by a tenant. The query filter
    /// makes system roles visible, so the check below is what stops the deletion - visibility
    /// and mutability are not the same permission.
    /// </remarks>
    [HttpDelete("{publicId:guid}")]
    [HasPermission(Permissions.RolesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        var role = await _db.Roles
            .FirstOrDefaultAsync(r => r.PublicId == publicId, ct)
            .ConfigureAwait(false);

        if (role is null)
        {
            return NotFound();
        }

        if (role.IsSystemRole)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "System roles cannot be modified or deleted.",
                Status = StatusCodes.Status403Forbidden,
            });
        }

        var tenantId = _tenantContext.TenantId;

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            AuditActions.RoleDeleted,
            tenantId,
            _currentUser.UserId,
            nameof(Role),

            // The public identifier, not the sequential one: the role row is gone by the time
            // anybody reads this, so the audit entry has to carry the handle the caller used.
            publicId.ToString(),
            ct: ct).ConfigureAwait(false);

        return NoContent();
    }
}
