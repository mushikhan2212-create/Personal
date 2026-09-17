using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarDealer.Application.AI;
using Microsoft.Extensions.Options;

namespace CarDealer.Integrations.AI;

/// <summary>
/// Ranks candidates through any OpenAI-shaped chat completions endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Groq is the reason this exists - its API is OpenAI-compatible, so the same adapter reaches
/// Groq, a self-hosted model server, or anything else speaking that shape, by changing
/// <see cref="AIOptions.BaseUrl"/> and nothing else. Named for the wire format rather than for
/// Groq, because the format is what it actually depends on.
/// </para>
///
/// <para>
/// Raw <c>HttpClient</c> rather than a vendor SDK, deliberately: the surface used here is one
/// POST and one JSON body, and a client library for a format three providers implement
/// slightly differently would buy nothing but a dependency.
/// </para>
///
/// <para>
/// <b>Unexercised.</b> No key was available when this was written, so it compiles but has
/// never run against a live endpoint. The likeliest first failure is structured-output support:
/// <c>response_format: json_schema</c> is honoured by some models and ignored by others, and an
/// endpoint that ignores it returns prose the parser will reject. That path is already handled
/// - it falls back and records the rejection - but it will look like a broken feature until
/// somebody reads the audit row, so check there first.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleRankingProvider : IAIProvider
{
    private readonly AIOptions _options;
    private readonly IHttpClientFactory _factory;

    public OpenAiCompatibleRankingProvider(IOptions<AIOptions> options, IHttpClientFactory factory)
    {
        _options = options.Value;
        _factory = factory;
    }

    /// <summary>The configured provider name, so the audit row says "groq" rather than a format.</summary>
    public string Name => string.IsNullOrWhiteSpace(_options.Provider)
        ? "openai-compatible"
        : _options.Provider.ToLowerInvariant();

    public string? Model => string.IsNullOrWhiteSpace(_options.Model) ? null : _options.Model;

    public bool IsConfigured => _options.IsConfigured && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Groq's endpoint when nothing else is configured.</summary>
    private string? BaseUrl => string.IsNullOrWhiteSpace(_options.BaseUrl)
        ? _options.Provider.Equals("groq", StringComparison.OrdinalIgnoreCase)
            ? "https://api.groq.com/openai/v1"
            : null
        : _options.BaseUrl.TrimEnd('/');

    public async Task<AIRankingResult> RankAsync(
        RankingRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return AIRankingResult.Failed(
                "No key or endpoint is configured for an OpenAI-compatible provider.", Name);
        }

        var call = await CallAsync(
                RankingPrompt.System,
                RankingPrompt.User(request),
                "vehicle_ranking",
                RankingPrompt.Schema,
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
            return AIExtractionResult.Failed(
                "No key or endpoint is configured for an OpenAI-compatible provider.", Name);
        }

        var call = await CallAsync(
                ExtractionPrompt.System,
                ExtractionPrompt.User(request),
                "customer_requirement",
                ExtractionPrompt.Schema,
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

    /// <summary>What one chat-completions call came back with.</summary>
    private sealed record Call(string? Content, AIUsage? Usage, string? Failure);

    /// <summary>
    /// One structured-output call, whatever it is asking for.
    /// </summary>
    /// <remarks>
    /// Shared between ranking and extraction because the transport is genuinely the same - a
    /// model, a ceiling, two messages and a schema - and the differences that matter are all in
    /// the prompt. Two copies would drift in exactly the places that are hardest to notice: a
    /// header, a timeout, or the rate-limit handling fixed on one side only.
    /// </remarks>
    private async Task<Call> CallAsync(
        string system, string user, string schemaName, JsonElement schema, CancellationToken ct)
    {
        using var http = _factory.CreateClient("ai-ranking");
        http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var body = new
        {
            model = _options.Model,
            max_tokens = _options.MaxTokens,

            // Deterministic-leaning. An answer that changes between identical calls is not
            // something anybody can refer back to, and creativity is not what is being asked
            // for in either of these tasks.
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = schemaName,
                    strict = true,
                    schema,
                },
            },
        };

        using var response = await http
            .PostAsJsonAsync($"{BaseUrl}/chat/completions", body, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // The body is included because these endpoints put the useful part there - an
            // unknown model id or a decommissioned one both arrive as a 400 whose status alone
            // says nothing. The rate limit is the exception: see ProviderErrors.
            return new Call(
                null,
                null,
                response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => ProviderErrors.RateLimit(Name, detail),

                    // 401 only. A 403 on this path is usually not the provider at all - a
                    // corporate proxy refusing the host answers with one, and its body names
                    // the host it blocked, which is the whole diagnosis. Paraphrasing that as
                    // "check your key" would send somebody looking in the wrong place.
                    HttpStatusCode.Unauthorized => ProviderErrors.Unauthorized(Name, _options.Model),
                    _ => $"{Name} returned {(int)response.StatusCode}: {Shorten(detail)}",
                });
        }

        var completion = await response.Content
            .ReadFromJsonAsync<ChatCompletion>(ct)
            .ConfigureAwait(false);

        var usage = completion?.Usage is null
            ? null
            : new AIUsage
            {
                InputTokens = completion.Usage.PromptTokens,
                OutputTokens = completion.Usage.CompletionTokens,
            };

        return new Call(completion?.Choices?.FirstOrDefault()?.Message?.Content, usage, null);
    }

    private static string Shorten(string text)
        => text.Length <= 400 ? text : text[..400];

    private sealed record ChatCompletion
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; init; }

        [JsonPropertyName("usage")]
        public UsageBlock? Usage { get; init; }
    }

    private sealed record Choice
    {
        [JsonPropertyName("message")]
        public ChoiceMessage? Message { get; init; }
    }

    private sealed record ChoiceMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed record UsageBlock
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; init; }
    }
}
