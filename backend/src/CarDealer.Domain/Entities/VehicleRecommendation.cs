using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// One car's place in a ranking, as it was shown to a salesperson.
/// </summary>
/// <remarks>
/// <para>
/// SQL schema spec section VehicleRecommendations, kept deliberately empty until now: filling
/// it with a deterministic filter's output would have been putting Phase 1 data in Phase 2's
/// table and calling it a recommendation.
/// </para>
///
/// <para>
/// Persisted rather than recomputed, and the reason is not the cost of the call. A salesperson
/// quotes a customer from what they saw on screen. If refreshing produced a different order
/// with different reasons, the ranking would be something nobody could rely on or refer back
/// to - and "the system told me it was the best fit" needs to still be true tomorrow.
/// Re-ranking is therefore an explicit act, not a side effect of opening a page.
/// </para>
///
/// <para>
/// <see cref="Source"/> records whether a row came from the model or from the deterministic
/// fallback. Without it, a fallback ordering stored in this table is indistinguishable from a
/// ranking somebody paid for, and the fallback rate is the number that decides whether a
/// provider is worth keeping.
/// </para>
/// </remarks>
public class VehicleRecommendation : Entity, ITenantScoped
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public long CustomerRequirementId { get; set; }

    public CustomerRequirement CustomerRequirement { get; set; } = null!;

    public long VehicleId { get; set; }

    public Vehicle Vehicle { get; set; } = null!;

    /// <summary>How well it fits, 0 to 1.</summary>
    public decimal Score { get; set; }

    /// <summary>1 is best, contiguous across the set.</summary>
    public int Rank { get; set; }

    /// <summary>The short phrases, as a JSON array of strings.</summary>
    public string? ReasonsJson { get; set; }

    public RecommendationSource Source { get; set; } = RecommendationSource.Unknown;

    /// <summary>The call that produced this, or null for a deterministic fallback row.</summary>
    public long? AIRequestId { get; set; }

    public AIRequest? AIRequest { get; set; }
}
