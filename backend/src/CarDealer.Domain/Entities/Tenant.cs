using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

public class Tenant : AuditableEntity, IPubliclyAddressable
{
    public Guid PublicId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>URL-safe identifier. Unique across the system (SQL schema spec section 6).</summary>
    public string Slug { get; set; } = string.Empty;

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public string DefaultCurrencyCode { get; set; } = "USD";

    public string DefaultCountryCode { get; set; } = "JP";

    public ICollection<TenantUser> Memberships { get; set; } = new List<TenantUser>();
}
