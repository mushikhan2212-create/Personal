using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// One tenant's commercial state over a global vehicle: their price, their visibility, their
/// notes (decision D1's overlay).
/// </summary>
/// <remarks>
/// Strictly tenant-owned - nothing here is ever visible to another tenant, which is what lets
/// the vehicle rows themselves be shared. This is the table that makes a global catalog
/// commercially usable: the car is shared, the markup is not.
/// </remarks>
public class TenantVehicle : AuditableEntity, ITenantScoped
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public long VehicleId { get; set; }

    public Vehicle Vehicle { get; set; } = null!;

    /// <summary>The tenant's own retail price, independent of any source listing.</summary>
    public decimal? TenantPrice { get; set; }

    public string? TenantCurrencyCode { get; set; }

    /// <summary>Overrides the canonical status for this tenant only.</summary>
    public VehicleStatus? TenantStatus { get; set; }

    /// <summary>Excludes the vehicle from this tenant's search without affecting anyone else.</summary>
    public bool IsHidden { get; set; }

    public bool IsPinned { get; set; }

    public string? InternalNotes { get; set; }
}
