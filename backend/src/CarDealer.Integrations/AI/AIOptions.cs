namespace CarDealer.Integrations.AI;

/// <summary>Which AI provider to use, and how to reach it.</summary>
/// <remarks>
/// <para>
/// The key is read from configuration but must never be <b>written</b> into
/// <c>appsettings*.json</c> - environment variable, user secrets or <c>backend/.env</c> only,
/// the same rule the vehicle-source credentials follow. A key in a settings file is a key in
/// git history, and git history is permanent.
/// </para>
///
/// <para>
/// <see cref="BaseUrl"/> exists because Groq is OpenAI-compatible: the same adapter reaches
/// Groq, a local model server, or any other OpenAI-shaped endpoint by pointing it somewhere
/// else. Anthropic is not OpenAI-shaped and has its own adapter.
/// </para>
/// </remarks>
public sealed class AIOptions
{
    public const string SectionName = "AI";

    /// <summary>"anthropic", "groq", or empty for none.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The model id, exactly as the provider spells it.
    /// </summary>
    /// <remarks>
    /// Deliberately not defaulted per provider. Model lineups change faster than this code
    /// will, and a stale default that silently resolves to a retired model is worse than a
    /// startup error telling somebody to name one.
    /// </remarks>
    public string Model { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Overrides the provider's default endpoint. Required for OpenAI-compatible hosts.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>How long to wait before giving up and falling back.</summary>
    /// <remarks>
    /// Short on purpose. A salesperson pressing a button is waiting at a screen, and a ranking
    /// that arrives after they have given up is worth less than the deterministic list they
    /// would have had immediately.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Provider)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Model);
}
