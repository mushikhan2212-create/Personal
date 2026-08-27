using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>Media for a <see cref="Vehicle"/>.</summary>
/// <remarks>
/// TenantId is nullable deliberately. This table FKs to VehicleId, so an image of a global
/// vehicle must itself be global - leaving it non-nullable would make images of global
/// vehicles unrepresentable (docs/spec/04-schema-delta.md section 1.1).
///
/// Only the URL is stored, not the bytes. Whether images may be re-hosted or re-served is an
/// unresolved licensing question (open item O1).
/// </remarks>
public class VehicleImage : Entity, IOptionallyTenantScoped
{
    public long? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    /// <summary>Persisted computed column, ISNULL(TenantId, 0). Kept for consistent index keys.</summary>
    public long TenantScope { get; private set; }

    public long VehicleId { get; set; }

    public Vehicle Vehicle { get; set; } = null!;

    public string ImageUrl { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public string? ImageType { get; set; }

    /// <summary>The source's own id for this image, used to avoid re-inserting on re-sync.</summary>
    public string? SourceImageId { get; set; }
}
