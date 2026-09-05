using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// One person's decision to leave a source out of their own searches.
/// </summary>
/// <remarks>
/// A view preference, not a policy. It changes what this user sees and nothing else: two
/// salespeople at the same dealership can have different sources muted, and neither affects
/// the other or any other tenant. That is why changing it needs no permission beyond being
/// able to search at all - it cannot hide anything from anybody else.
///
/// Not tenant-scoped, and deliberately. A user is a global identity (decision D2) and this row
/// belongs to the person, not to a membership. A tenant-owned source is only ever visible
/// inside its own tenant anyway, so a preference against it can never leak somewhere it does
/// not apply.
///
/// Absence means enabled. A source nobody has touched is on for everyone, which is what makes
/// a newly registered source visible immediately rather than requiring every user to opt in
/// before the cars an admin just imported appear for anyone.
/// </remarks>
public class UserVehicleSourcePreference : AuditableEntity
{
    public long UserId { get; set; }

    public User User { get; set; } = null!;

    public long VehicleSourceId { get; set; }

    public VehicleSource VehicleSource { get; set; } = null!;

    /// <summary>False hides this source's listings from this user's searches.</summary>
    public bool IsEnabled { get; set; } = true;
}
