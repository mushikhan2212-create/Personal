using CarDealer.Application.AI;

namespace CarDealer.Infrastructure.AI;

/// <summary>
/// The provider used when no key has been set.
/// </summary>
/// <remarks>
/// A real implementation of the interface rather than a null reference, because "no provider"
/// is an ordinary state of this system rather than a misconfiguration to crash on: the feature
/// ships before the owner has chosen between Anthropic and Groq, and the screens describe that
/// honestly. Registering this keeps every call site free of null checks and keeps the
/// no-provider path exercised by the same tests as every other failure.
/// </remarks>
public sealed class UnconfiguredAIProvider : IAIProvider
{
    public string Name => "none";

    public string? Model => null;

    public bool IsConfigured => false;

    public Task<AIRankingResult> RankAsync(RankingRequest request, CancellationToken ct = default)
        => Task.FromResult(AIRankingResult.Failed(
            "No AI provider is configured. Set an API key to enable ranking.", Name));
}
