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
    /// Administer vehicle sources: register, sync, import into and delete them.
    /// </summary>
    /// <remarks>
    /// Held by Admin and Tenant Owner only. A shared source writes the global catalog that
    /// every tenant reads (decision D1), so registering one or importing a file into it
    /// publishes cars to people the importer has never met - that is an administrative act,
    /// not a browsing one, and it also spends provider quota.
    ///
    /// Reading that catalog is <see cref="VehiclesRead"/>, which every role holds, and choosing
    /// which of those sources feed your own searches needs nothing more than that: see
    /// MySourcesController.
    /// </remarks>
    public const string VehiclesSync = "vehicles.sync";

    /// <summary>
    /// Review suggested duplicate vehicles, and merge or reject them.
    /// </summary>
    /// <remarks>
    /// Held by Admin and Tenant Owner only, on the same reasoning as
    /// <see cref="VehiclesSync"/>: most of the catalogue is the global rows decision D1 shares,
    /// so merging two of them changes what every tenant sees, and archiving the wrong one hides
    /// a car from people the reviewer has never met. That is an administrative act.
    ///
    /// Separate from <see cref="VehiclesSync"/> rather than folded into it because the two are
    /// different powers over the catalogue - one feeds it, the other edits which of its rows are
    /// the same car - and a dealer may reasonably want somebody importing stock who is not also
    /// deciding its identity.
    /// </remarks>
    public const string VehiclesMerge = "vehicles.merge";

    /// <summary>Read the tenant's customers and their requirements.</summary>
    public const string CustomersRead = "customers.read";

    /// <summary>
    /// Create, edit and delete customers and requirements.
    /// </summary>
    /// <remarks>
    /// Separate from reading because deleting a customer is irreversible by design - erasure has
    /// to mean erasure - and because a read-only account exists precisely so someone can be
    /// shown the book without being able to rewrite it.
    /// </remarks>
    public const string CustomersManage = "customers.manage";

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
        [VehiclesSync] = "Register, sync, import into and delete vehicle sources",
        [VehiclesMerge] = "Review suggested duplicate vehicles and merge them",
        [CustomersRead] = "View customers and their requirements",
        [CustomersManage] = "Create, edit and delete customers and requirements",
    };

    /// <summary>
    /// Permission grants per system role. The seeder applies this exactly: codes missing from a
    /// system role are added, and codes it holds that are not listed here are revoked.
    /// </summary>
    /// <remarks>
    /// Authoritative rather than a starting point, because a grant that can only ever be added
    /// makes narrowing a role impossible - editing this table would leave the old, wider grant
    /// sitting in every database that had already been seeded. Tenant-defined roles are not
    /// derived from this table and the seeder never touches them.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string[]> SystemRoleGrants =
        new Dictionary<string, string[]>
        {
            [SystemRoles.TenantOwner] = [.. All.Keys],
            [SystemRoles.Admin] =
            [
                TenantsRead, UsersRead, UsersManage, RolesRead, RolesManage, AuditRead,
                VehiclesRead, VehiclesSync, VehiclesMerge, CustomersRead, CustomersManage,
            ],
            // Sales Manager reads the catalog like everyone else but does not administer
            // sources: importing publishes cars into the shared catalog, which is Admin's call.
            [SystemRoles.SalesManager] =
                [TenantsRead, UsersRead, RolesRead, VehiclesRead, CustomersRead, CustomersManage],
            // Selling is the job. A salesperson who cannot record who they are selling to
            // has no product here.
            [SystemRoles.Salesperson] =
                [TenantsRead, UsersRead, VehiclesRead, CustomersRead, CustomersManage],

            // ReadOnly can search. Withholding it would make the role useless in a product
            // whose main screen is a search.
            [SystemRoles.ReadOnly] = [TenantsRead, VehiclesRead, CustomersRead],
        };
}
