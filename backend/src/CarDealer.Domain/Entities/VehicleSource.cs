using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A place vehicles come from: an API provider, a dealer file feed, or manual entry.
/// </summary>
/// <remarks>
/// TenantId is nullable because a source can be shared across tenants (SQL schema spec
/// section 3). That nullability is what decision D1 builds the global catalog on: vehicles
/// from a shared source are global, vehicles from a tenant's own source are private.
///
/// Credentials never live here - only a reference to them, on
/// <see cref="VehicleSourceConfiguration"/>, which is always tenant-scoped.
/// </remarks>
public class VehicleSource : AuditableEntity, IOptionallyTenantScoped
{
    /// <summary>Null for a shared source; set for a tenant's own.</summary>
    public long? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Persisted computed column, ISNULL(TenantId, 0). The base schema's UNIQUE(TenantId, Code)
    /// permits duplicate shared-source codes, because SQL Server treats those null TenantIds as
    /// distinct. Uniqueness is enforced over this instead
    /// (docs/spec/04-schema-delta.md section 1.2).
    /// </summary>
    public long TenantScope { get; private set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Stable identifier, unique within the tenant scope.</summary>
    public string Code { get; set; } = string.Empty;

    public VehicleSourceProviderType ProviderType { get; set; }

    public VehicleSourceType SourceType { get; set; }

    public string? BaseUrl { get; set; }

    public bool IsShared { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<VehicleSourceConfiguration> Configurations { get; set; }
        = new List<VehicleSourceConfiguration>();
}
