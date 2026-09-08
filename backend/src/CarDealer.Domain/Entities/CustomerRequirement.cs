using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// What one customer is looking for (SQL schema spec section CustomerRequirements).
/// </summary>
/// <remarks>
/// A requirement is a saved search with a person attached. That is not a loose analogy - it is
/// how matching is implemented: these fields map onto <c>VehicleSearchQuery</c> and run through
/// the same <c>ISearchProvider</c> the vehicle screen uses, so a match inherits tenant scoping,
/// the caller's muted sources and the grouping-by-car behaviour without a second query path
/// existing to drift out of step.
///
/// That is also why the enum-typed fields are the catalog's own enums rather than free text.
/// A requirement whose fuel type cannot be compared with a vehicle's fuel type is not a
/// requirement, it is a note.
///
/// A customer may have several open at once - master prompt section 9 requires it, and it is
/// how the trade works: a buyer wants a Hiace for the business and a Vitz for their daughter.
/// </remarks>
public class CustomerRequirement : AuditableEntity, ITenantScoped
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public long CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    /// <summary>What the salesperson calls it: "Hiace for the shop", "wife's car".</summary>
    public string? Name { get; set; }

    // --- What they want -------------------------------------------------------------------

    public string? Make { get; set; }

    public string? Model { get; set; }

    public string? Variant { get; set; }

    public string? BodyType { get; set; }

    public string? ExteriorColor { get; set; }

    public int? MinYear { get; set; }

    public int? MaxYear { get; set; }

    public int? MinMileage { get; set; }

    public int? MaxMileage { get; set; }

    public Transmission? Transmission { get; set; }

    public FuelType? FuelType { get; set; }

    // --- What they will pay ---------------------------------------------------------------

    /// <summary>
    /// Budget floor, in <see cref="CurrencyCode"/>.
    /// </summary>
    /// <remarks>
    /// Matching compares this against the listing's normalized base price (decision D6), which
    /// is the only price comparable across currencies. A listing whose currency has no pinned
    /// exchange rate is therefore excluded from a budgeted requirement rather than converted at
    /// a guess - the same rule the vehicle search already applies, for the same reason.
    /// </remarks>
    public decimal? MinPrice { get; set; }

    public decimal? MaxPrice { get; set; }

    public string? CurrencyCode { get; set; }

    // --- Where it is going ----------------------------------------------------------------

    /// <summary>
    /// ISO 3166-1 alpha-2 for the destination market.
    /// </summary>
    /// <remarks>
    /// Recorded, not enforced. Destination markets apply age limits, emissions rules and
    /// steering-side restrictions that vary by country and change over time, and nobody owns
    /// that rules table yet (open item O10). Encoding a rule wrongly is worse than not encoding
    /// it, because it silently hides stock the customer could legally have bought - so this
    /// stays a note on the requirement until someone owns the rules.
    /// </remarks>
    public string? DestinationCountryCode { get; set; }

    public string? DestinationCity { get; set; }

    /// <summary>
    /// What the customer actually said, before anyone turned it into fields.
    /// </summary>
    /// <remarks>
    /// Kept verbatim because the structured fields are lossy and the original is evidence: when
    /// a match looks wrong, this is what says whether the requirement was captured wrongly or
    /// the matching is at fault. Phase 2's AI extraction reads this rather than replacing it.
    /// </remarks>
    public string? RawRequirementText { get; set; }

    public RequirementStatus Status { get; set; } = RequirementStatus.Open;
}
