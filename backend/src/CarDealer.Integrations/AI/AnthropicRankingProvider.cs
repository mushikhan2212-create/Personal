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

        var call = await CallAsync(
                RankingPrompt.System,
                RankingPrompt.User(request),
                RankingPrompt.SchemaMembers(),
                ct)
            .ConfigureAwait(false);

        if (call.Failure is not null)
        {
            return AIRankingResult.Failed(call.Failure, Name, _options.Model);
        }

        var ranked = RankingResponse.Parse(call.Content, out var failure);

        return new AIRankingResult
        {
            Ranked = ranked,
            Failure = ranked is null ? failure : null,
            Usage = call.Usage,
            Provider = Name,
            Model = _options.Model,
        };
    }

    public async Task<AIExtractionResult> ExtractAsync(
        ExtractionRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return AIExtractionResult.Failed("No Anthropic key is configured.", Name);
        }

        var call = await CallAsync(
                ExtractionPrompt.System,
                ExtractionPrompt.User(request),
                ExtractionPrompt.SchemaMembers(),
                ct)
            .ConfigureAwait(false);

        if (call.Failure is not null)
        {
            return AIExtractionResult.Failed(call.Failure, Name, _options.Model);
        }

        var fields = ExtractionResponse.Parse(call.Content, out var failure);

        return new AIExtractionResult
        {
            Fields = fields,
            Failure = fields is null ? failure : null,
            Usage = call.Usage,
            Provider = Name,
            Model = _options.Model,
        };
    }

    /// <summary>What one message call came back with.</summary>
    private sealed record Call(string? Content, AIUsage? Usage, string? Failure);

    /// <summary>
    /// One schema-constrained message, whatever it is asking for.
    /// </summary>
    /// <remarks>
    /// Shared between ranking and extraction: the differences that matter are in the prompt and
    /// the schema, and two copies of the transport would drift in the places hardest to notice -
    /// the timeout, or the refusal check fixed on one side only.
    /// </remarks>
    private async Task<Call> CallAsync(
        string system,
        string user,
        Dictionary<string, System.Text.Json.JsonElement> schema,
        CancellationToken ct)
    {
        AnthropicClient client = new() { ApiKey = _options.ApiKey };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var response = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = _options.Model,

                MaxTokens = _options.MaxTokens,

                // The standing instructions, kept out of the user turn so they stay identical
                // between calls. No cache breakpoint: these prompts are well below the minimum
                // cacheable prefix, so marking one would imply a saving that does not happen.
                System = new List<TextBlockParam> { new() { Text = system } },

                Messages =
                [
                    new() { Role = Role.User, Content = user },
                ],

                // A schema rather than a request for JSON. Asking politely for JSON and parsing
                // whatever comes back is how an answer arrives wrapped in an apology.
                OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat { Schema = schema },
                },
            },
            cancellationToken: timeout.Token).ConfigureAwait(false);

        // Checked before the content is read. A refusal is an HTTP 200 whose content is not the
        // answer, so reading it first would parse an explanation as a result.
        if (response.StopReason == "refusal")
        {
            return new Call(
                null,
                null,
                "The model declined the request "
                + $"({response.StopDetails?.Category ?? "no category"}).");
        }

        var text = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

        return new Call(
            text,
            new AIUsage
            {
                InputTokens = (int)response.Usage.InputTokens,
                OutputTokens = (int)response.Usage.OutputTokens,
            },
            null);
    }
}
