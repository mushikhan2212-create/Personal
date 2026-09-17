using CarDealer.Application.AI;
using Microsoft.Extensions.Options;

namespace CarDealer.Integrations.AI;

/// <summary>
/// Builds a provider from a set of options, for callers that choose their own.
/// </summary>
/// <remarks>
/// Registration cannot use this - it registers a <i>type</i> against the container and resolves
/// one provider for the process. This is for the two places that need a provider pointed
/// somewhere else on purpose: the model probe, which tries several in a row, and the extraction
/// workbench, which exists so a model can be swapped without restarting anything.
///
/// The rule it encodes is registration's own: Anthropic has its own shape, and everything else
/// is assumed to speak OpenAI chat completions, which is what Groq and most self-hosted servers
/// do. Keeping that in one place stops the three callers drifting on what "groq" means.
/// </remarks>
public static class AIProviderFactory
{
    public static IAIProvider Create(AIOptions options, IHttpClientFactory factory)
        => options.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicRankingProvider(Options.Create(options))
            : new OpenAiCompatibleRankingProvider(Options.Create(options), factory);
}
