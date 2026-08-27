using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// One tenant's configuration for a source, including how to reach its credentials.
/// </summary>
/// <remarks>
/// Always tenant-scoped even when the source itself is shared: twenty tenants may read the
/// same Carapis source, but each authenticates as itself and syncs on its own schedule
/// (SQL schema spec section 9).
///
/// CredentialReference is a pointer into a secret store, never a secret
/// (master prompt section 14, acceptance criterion I3).
/// </remarks>
public class VehicleSourceConfiguration : AuditableEntity, ITenantScoped
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public long VehicleSourceId { get; set; }

    public VehicleSource VehicleSource { get; set; } = null!;

    /// <summary>Provider-specific settings: filters, quotas, permitted sub-sources.</summary>
    public string? ConfigurationJson { get; set; }

    /// <summary>Key or path in the secret store. Never the credential itself.</summary>
    public string? CredentialReference { get; set; }

    public bool SyncEnabled { get; set; }

    public int? SyncIntervalMinutes { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }

    public DateTime? LastFailureAtUtc { get; set; }

    public string? LastError { get; set; }
}
