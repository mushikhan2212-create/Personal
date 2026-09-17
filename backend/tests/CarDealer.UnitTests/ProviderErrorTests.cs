using CarDealer.Integrations.AI;

namespace CarDealer.UnitTests;

/// <summary>
/// The one provider failure a salesperson meets often enough to need words for.
/// </summary>
public sealed class ProviderErrorTests
{
    /// <summary>The real body, verbatim, from the run that prompted this.</summary>
    private const string Real = """
        {"error":{"message":"Request too large for model `qwen/qwen3.8-27b` in organization
        `org_redacted` service tier `on_demand` on output tokens per minute (OTPM): Limit 1000,
        Requested 1200. The request's expected output tokens exceed the enforced limit; reduce
        max_tokens (or the request's expected output) and try again. Need more tokens? Upgrade
        to Dev Tier today at https://console.groq.com/settings/billing","type":"requests"}}
        """;

    [Fact]
    public void The_two_numbers_that_matter_survive()
    {
        var message = ProviderErrors.RateLimit("groq", Real);

        Assert.Contains("1,000 output tokens a minute", message);
        Assert.Contains("asked for 1,200", message);
    }

    [Fact]
    public void It_names_the_setting_to_change()
    {
        // The fix is a config value, and the raw body never says which one. A message that
        // reported the failure without naming AI__MaxTokens would be no more actionable than
        // the JSON it replaced.
        var message = ProviderErrors.RateLimit("groq", Real);

        Assert.Contains("AI__MaxTokens below 1,000", message);
        Assert.Contains("AI__MaxCandidates", message);
    }

    [Fact]
    public void The_upgrade_url_and_the_organization_id_are_dropped()
    {
        // Neither belongs on a screen a salesperson is looking at, and the truncated URL is
        // what made the old message read as gibberish.
        var message = ProviderErrors.RateLimit("groq", Real);

        Assert.DoesNotContain("https://", message);
        Assert.DoesNotContain("org_", message);
    }

    [Fact]
    public void A_body_without_the_figures_still_gives_advice()
    {
        // Providers word these differently and the wording changes without notice. Failing to
        // parse must degrade to something useful, not to an empty sentence.
        var message = ProviderErrors.RateLimit("groq", "{\"error\":\"slow down\"}");

        Assert.Contains("rate limit", message);
        Assert.Contains("AI__MaxCandidates", message);
    }

    [Fact]
    public void The_provider_is_named()
    {
        Assert.StartsWith("anthropic", ProviderErrors.RateLimit("anthropic", Real));
    }

    [Fact]
    public void A_rejected_key_says_where_to_look()
    {
        // "Invalid API Key" is all the provider says, and it sends people to their account when
        // the cause is usually which file the key is in.
        var message = ProviderErrors.Unauthorized("groq", "qwen/qwen3.8-27b");

        Assert.Contains("rejected the key", message);
        Assert.Contains("qwen/qwen3.8-27b", message);
        Assert.Contains("backend/.env", message);
        Assert.Contains("user secrets", message);
    }

    [Fact]
    public void A_rejected_key_with_no_model_still_reads()
    {
        Assert.DoesNotContain("for model", ProviderErrors.Unauthorized("groq", null));
    }
}
