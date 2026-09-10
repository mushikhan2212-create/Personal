using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A named set of permissions. System roles have a null <see cref="TenantId"/>; tenants may
/// define their own (schema delta section 2.2).
/// </summary>
/// <remarks>
/// Name is unique per TenantScope rather than globally, so two tenants can each have a role
/// called "Sales Manager". The original schema's global unique constraint on Name made that
/// impossible.
/// </remarks>
public class Role : Entity, IPubliclyAddressable
{
    /// <summary>
    /// Stable external identifier, so the route never exposes the sequential key.
    /// </summary>
    /// <remarks>
    /// Present because this entity is addressed at the <b>top level</b> of a route, where the
    /// id is the only thing identifying the record - see decision D17. Entities reached through
    /// a nested route keep their integer keys, because the parent's own PublicId already gates
    /// access to them.
    /// </remarks>
    public Guid PublicId { get; set; }

    /// <summary>Null means a system role, which tenants may not edit or delete.</summary>
    public long? TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsSystemRole => TenantId is null;

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}

/// <summary>
/// Well-known system role names, seeded deterministically (SQL schema spec section 12).
/// </summary>
public static class SystemRoles
{
    public const string TenantOwner = "TenantOwner";
    public const string Admin = "Admin";
    public const string SalesManager = "SalesManager";
    public const string Salesperson = "Salesperson";
    public const string ReadOnly = "ReadOnly";

    public static readonly IReadOnlyList<string> All =
    [
        TenantOwner,
        Admin,
        SalesManager,
        Salesperson,
        ReadOnly,
    ];
}
