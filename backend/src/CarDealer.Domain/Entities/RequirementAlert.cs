using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A car that appeared after a customer said what they were looking for, and fits it.
/// </summary>
/// <remarks>
/// The feature <see href="../../../docs/spec/05-open-items.md">O11</see> asks for: stock in this
/// trade moves fast, and the dealer who calls first usually gets the sale. Matching a
/// requirement against the catalogue on demand already existed; what did not was anything that
/// tells a salesperson a match has appeared while they were doing something else.
///
/// <para>
/// <b>An alert is about new stock, never about the catalogue.</b> A requirement written today
/// against a catalogue of a hundred cars typically matches dozens of them, and turning those
/// into alerts would produce forty notifications about cars the salesperson can already see on
/// the requirement's own matches list. So an alert is raised only for a listing whose
/// <c>FirstSeenAtUtc</c> is later than the requirement's <c>CreatedAtUtc</c>: the car arrived
/// after the customer asked. That single rule is what separates an alert from a search result.
/// </para>
///
/// <para>
/// Deliberately not <c>VehicleRecommendations</c>, which the schema defines with <c>Score</c>,
/// <c>ReasonsJson</c> and <c>AIRequestId</c>. That table is Phase 2's AI scoring, and filling
/// it with a deterministic Phase 1 match would be building Phase 2's table with the wrong data
/// in it.
/// </para>
/// </remarks>
public class RequirementAlert : AuditableEntity, ITenantScoped
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public long CustomerRequirementId { get; set; }

    public CustomerRequirement CustomerRequirement { get; set; } = null!;

    /// <summary>
    /// The car, not the listing.
    /// </summary>
    /// <remarks>
    /// One physical car offered by three exporters is one alert, because it is one thing to
    /// call the customer about. The listing that triggered it can change - a source can
    /// withdraw its offer while another keeps selling the same car - and the alert survives
    /// that, which it would not if it pointed at a listing.
    /// </remarks>
    public long VehicleId { get; set; }

    public Vehicle Vehicle { get; set; } = null!;

    /// <summary>When the scan found it. Distinct from when the listing first appeared.</summary>
    public DateTime MatchedAtUtc { get; set; }

    /// <summary>
    /// The base-currency price when the alert was raised, or null if it had none.
    /// </summary>
    /// <remarks>
    /// Copied rather than read live. An alert says "a car you wanted appeared at $6,020"; if
    /// the exporter later raises the price, rewriting the alert to match would make the record
    /// of what the salesperson was told untrue.
    /// </remarks>
    public decimal? PriceBaseAtMatch { get; set; }

    public string? BaseCurrencyCode { get; set; }

    /// <summary>Null until somebody has looked at it. This is what the unseen count counts.</summary>
    public DateTime? SeenAtUtc { get; set; }

    /// <summary>Who marked it seen, so a shared inbox says who has already dealt with it.</summary>
    public long? SeenByUserId { get; set; }

    public User? SeenByUser { get; set; }
}
