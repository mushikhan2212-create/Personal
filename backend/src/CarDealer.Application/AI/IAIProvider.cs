namespace CarDealer.Application.AI;

/// <summary>
/// The seam every AI call goes through (master prompt section 11's provider-agnostic layer).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape of abstraction as <c>IVehicleSourceProvider</c> and
/// <c>IMessagingProvider</c>, and for the same reason: the provider is a commercial decision
/// that will be revisited, and nothing above this line should have to change when it is. The
/// owner is choosing between Anthropic and Groq on cost, and that choice must not reach the
/// ranking rules, the guards, the persistence or the screen.
/// </para>
///
/// <para>
/// <see cref="IsConfigured"/> is on the interface rather than discovered by calling and
/// failing, because "no key has been set" is an ordinary state of this system today - the
/// screens have to describe it rather than treat it as an error.
/// </para>
/// </remarks>
public interface IAIProvider
{
    /// <summary>Short name for the audit row, e.g. "anthropic" or "groq".</summary>
    string Name { get; }

    /// <summary>The model this provider is pointed at, or null when unconfigured.</summary>
    string? Model { get; }

    /// <summary>False when no credential is available, so callers can say so plainly.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Ranks an already-filtered candidate set.
    /// </summary>
    /// <remarks>
    /// Ranks, never selects. The candidates are produced by the deterministic filters and are
    /// the complete set the caller will display; this reorders them and says why. An
    /// implementation must not add, remove or substitute cars - and <see cref="RankingGuards"/>
    /// checks that it did not rather than trusting it.
    /// </remarks>
    Task<AIRankingResult> RankAsync(RankingRequest request, CancellationToken ct = default);
}
