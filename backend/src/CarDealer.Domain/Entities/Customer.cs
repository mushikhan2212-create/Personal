using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A person the dealer sells to (master prompt section 9, SQL schema spec section Customers).
/// </summary>
/// <remarks>
/// Strictly tenant-owned, and this is the entity where that distinction earns its keep. A
/// shared vehicle catalog is the product (decision D1); a shared customer list is a breach. So
/// <see cref="TenantId"/> is non-nullable and its query filter is flat equality, never the
/// "null means everyone" rule the catalog tables use - a customer row can never be global,
/// because there is no value of TenantId that would make it so.
///
/// This is also the first personal data the platform holds. Names, phone numbers and email
/// addresses for a deliberately multi-country customer base put
/// <see href="../../../docs/spec/05-open-items.md">O3</see> on the critical path: retention,
/// residency and the controller/processor question are unanswered policy. What the schema does
/// about that now is stay minimal - only the fields section 9 asks for - and support real
/// deletion rather than a flag, so that whatever the answer turns out to be, erasing a customer
/// erases them.
/// </remarks>
public class Customer : AuditableEntity, ITenantScoped
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    /// <summary>Stable external identifier, so URLs never expose the sequential key.</summary>
    public Guid PublicId { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    /// <summary>
    /// Primary contact number, and in this trade usually the WhatsApp number too.
    /// </summary>
    /// <remarks>
    /// Not unique, even within a tenant. A household shares a number, a company reception
    /// desk is on twenty records, and a unique index here would reject a legitimate second
    /// customer. Duplicate detection belongs to the import path, where a human can adjudicate.
    /// </remarks>
    public string? Phone { get; set; }

    public string? Email { get; set; }

    /// <summary>ISO 3166-1 alpha-2. Where the customer is, not where the car is going.</summary>
    public string? CountryCode { get; set; }

    public string? City { get; set; }

    /// <summary>Language to correspond in, for the messaging Phase 1 adds later.</summary>
    public string? PreferredLanguage { get; set; }

    public CustomerStatus Status { get; set; } = CustomerStatus.Lead;

    public LeadSource LeadSource { get; set; } = LeadSource.Unknown;

    /// <summary>The salesperson who owns the relationship. Null means unassigned.</summary>
    /// <remarks>
    /// A user is a global identity (decision D2), so this being set does not imply the user is
    /// still a member of this tenant - a leaver's customers stay assigned until someone
    /// reassigns them, which is better than silently orphaning the relationship.
    /// </remarks>
    public long? AssignedUserId { get; set; }

    public User? AssignedUser { get; set; }

    public string? Notes { get; set; }

    public ICollection<CustomerRequirement> Requirements { get; set; } = new List<CustomerRequirement>();
}
