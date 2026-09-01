using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A single capability, referenced by code rather than by role name.
/// </summary>
/// <remarks>
/// Master prompt section 3 requires "users, roles and permissions". Acceptance criterion E1
/// requires that authorization checks read this table rather than testing role names, so
/// that a tenant defining a custom role does not need a code change.
/// </remarks>
public class Permission : Entity
{
    /// <summary>Dotted lowercase, e.g. <c>vehicles.read</c>. Globally unique.</summary>
    public string Code { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

/// <summary>
/// Permission codes known to Phase 0. Seeded deterministically.
/// </summary>
/// <remarks>
/// Phase 0 only defines permissions for what Phase 0 actually exposes: tenant, user, role
/// and audit administration. Vehicle, customer and messaging permissions arrive with the
/// features they guard, so that no permission exists without an endpoint behind it.
/// </remarks>
public static class Permissions
{
    public const string TenantsRead = "tenants.read";
    public const string TenantsManage = "tenants.manage";

    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";

    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";

    public const string AuditRead = "audit.read";

    /// <summary>Read the vehicle catalog. Granted to every role - search is the product.</summary>
    public const string VehiclesRead = "vehicles.read";

    /// <summary>
    /// Trigger a synchronization run against a vehicle source.
    /// </summary>
    /// <remarks>
    /// Separate from reading, and granted narrowly, because a sync spends provider quota and
    /// writes to the shared global catalog. It is not a browsing action.
    /// </remarks>
    public const string VehiclesSync = "vehicles.sync";

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        [TenantsRead] = "View tenant details and settings",
        [TenantsManage] = "Modify tenant details and settings",
        [UsersRead] = "View users and their memberships",
        [UsersManage] = "Invite, modify, suspend and remove users",
        [RolesRead] = "View roles and their permissions",
        [RolesManage] = "Create, modify and delete tenant roles",
        [AuditRead] = "Read the audit log",
        [VehiclesRead] = "Search and view the vehicle catalog",
        [VehiclesSync] = "Trigger a synchronization run against a vehicle source",
    };

    /// <summary>
    /// Default permission grants per system role, applied by the seeder.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> SystemRoleGrants =
        new Dictionary<string, string[]>
        {
            [SystemRoles.TenantOwner] = [.. All.Keys],
            [SystemRoles.Admin] =
            [
                TenantsRead, UsersRead, UsersManage, RolesRead, RolesManage, AuditRead,
                VehiclesRead, VehiclesSync,
            ],
            [SystemRoles.SalesManager] = [TenantsRead, UsersRead, RolesRead, VehiclesRead, VehiclesSync],
            [SystemRoles.Salesperson] = [TenantsRead, UsersRead, VehiclesRead],

            // ReadOnly can search. Withholding it would make the role useless in a product
            // whose main screen is a search.
            [SystemRoles.ReadOnly] = [TenantsRead, VehiclesRead],
        };
}
