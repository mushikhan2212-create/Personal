using Anthropic;
using Anthropic.Models.Messages;
using CarDealer.Application.AI;
using Microsoft.Extensions.Options;

namespace CarDealer.Integrations.AI;

/// <summary>
/// Ranks candidates using Claude, through the official Anthropic SDK.
/// </summary>
/// <remarks>
/// <para>
/// Transport only. Every rule about what a good ranking is lives in
/// <see cref="RankingPrompt"/>, and every rule about whether to believe one lives in
/// <see cref="RankingGuards"/> - this class turns a request into an HTTP call and the reply
/// into a <see cref="RankedVehicle"/> list. That split is what lets the provider be a
/// commercial decision.
/// </para>
///
/// <para>
/// <b>Unexercised.</b> No key was available when this was written, so it has been compiled but
/// never run against the API. Treat the first live call as the test: the failure modes to
/// expect are a model id the account cannot reach, and a structured-output shape the SDK
/// version spells differently.
/// </para>
/// </remarks>
public sealed class AnthropicRankingProvider : IAIProvider
{
    private readonly AIOptions _options;

    public AnthropicRankingProvider(IOptions<AIOptions> options) => _options = options.Value;

    public string Name => "anthropic";

    public string? Model => string.IsNullOrWhiteSpace(_options.Model) ? null : _options.Model;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<AIRankingResult> RankAsync(
        RankingRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return AIRankingResult.Failed("No Anthropic key is configured.", Name);
        }

        AnthropicClient client = new() { ApiKey = _options.ApiKey };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var response = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = _options.Model,

                MaxTokens = _options.MaxTokens,

                // The standing instructions, kept out of the user turn so they stay identical
                // between calls. No cache breakpoint: this prompt is well below the minimum
                // cacheable prefix, so marking it would imply a saving that does not happen.
                System = new List<TextBlockParam> { new() { Text = RankingPrompt.System } },

                Messages =
                [
                    new() { Role = Role.User, Content = RankingPrompt.User(request) },
                ],

                // A schema rather than a request for JSON. Asking politely for JSON and parsing
                // whatever comes back is how a ranking arrives wrapped in an apology.
                OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat { Schema = RankingPrompt.SchemaMembers() },
                },
            },
            cancellationToken: timeout.Token).ConfigureAwait(false);

        // Checked before the content is read. A refusal is an HTTP 200 whose content is not the
        // answer, so reading it first would parse an explanation as a ranking.
        if (response.StopReason == "refusal")
        {
            return AIRankingResult.Failed(
                $"The model declined the request ({response.StopDetails?.Category ?? "no category"}).",
                Name,
                _options.Model);
        }

        var text = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

        var ranked = RankingResponse.Parse(text, out var failure);

        var usage = new AIUsage
        {
            InputTokens = (int)response.Usage.InputTokens,
            OutputTokens = (int)response.Usage.OutputTokens,
        };

        if (ranked is null)
        {
            return new AIRankingResult
            {
                Failure = failure,
                Usage = usage,
                Provider = Name,
                Model = _options.Model,
            };
        }

        return new AIRankingResult
        {
            Ranked = ranked,
            Usage = usage,
            Provider = Name,
            Model = _options.Model,
        };
    }
}
