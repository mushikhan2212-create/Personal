using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A global identity. Membership in tenants comes from <see cref="TenantUser"/>, and roles
/// from <see cref="UserRole"/> (decision D2).
/// </summary>
/// <remarks>
/// There is deliberately no TenantId here. Email is globally unique, so one person uses one
/// login across every dealer they work for.
/// </remarks>
public class User : AuditableEntity, IPubliclyAddressable
{
    public Guid PublicId { get; set; }

    public string Email { get; set; } = string.Empty;

    /// <summary>PBKDF2-HMAC-SHA512 via ASP.NET Core's PasswordHasher. Never reversible.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string? Phone { get; set; }

    /// <summary>
    /// Global account state. Do not use for per-tenant suspension - see
    /// <see cref="TenantUser.MembershipStatus"/>.
    /// </summary>
    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTime? LastLoginAtUtc { get; set; }

    public ICollection<TenantUser> Memberships { get; set; } = new List<TenantUser>();

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
