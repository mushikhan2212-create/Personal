using CarDealer.Integrations.AI;

namespace CarDealer.UnitTests;

/// <summary>
/// The ways a correct key arrives wrong, and arrives as "Invalid API Key".
/// </summary>
/// <remarks>
/// Every case here was a real 401 waiting to happen, and none of them is visible by looking at
/// the configuration file. They are grouped in one place because the symptom is identical and
/// the instinct - check the account, regenerate the key - is wrong for all of them.
/// </remarks>
public sealed class AIOptionsTests
{
    private const string Key = "gsk_abcdefghijklmnopqrstuvwxyz0123456789";

    [Fact]
    public void A_carriage_return_from_a_Windows_env_file_is_removed()
    {
        // docker compose carries the line ending into the value, and the request then sends a
        // key with a control character on the end of it.
        Assert.Equal(Key, AIOptions.Clean(Key + "\r"));
        Assert.Equal(Key, AIOptions.Clean(Key + "\r\n"));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("'")]
    public void Quotation_marks_a_dotenv_file_keeps_are_removed(string quote)
    {
        // A .env file is not shell syntax. Quoting the value stores the quotes.
        Assert.Equal(Key, AIOptions.Clean($"{quote}{Key}{quote}"));
    }

    [Fact]
    public void A_pasted_space_is_removed()
    {
        Assert.Equal(Key, AIOptions.Clean($"  {Key} "));
        Assert.Equal(Key, AIOptions.Clean($"\" {Key} \""));
    }

    [Fact]
    public void Only_one_matched_pair_comes_off()
    {
        // A key that genuinely started and ended with a quote would be extraordinary, and
        // stripping repeatedly could eat a real character.
        Assert.Equal($"\"{Key}\"", AIOptions.Clean($"\"\"{Key}\"\""));
    }

    [Fact]
    public void An_unmatched_quote_is_left_alone()
    {
        // Half a pair is somebody's typo, and silently repairing it would hide the typo while
        // still sending a key the provider rejects.
        Assert.Equal($"\"{Key}", AIOptions.Clean($"\"{Key}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_stays_nothing(string? value)
        => Assert.Equal(string.Empty, AIOptions.Clean(value));

    [Fact]
    public void An_ordinary_value_is_untouched()
    {
        // The anti-vacuity case: without it everything above could pass by returning "".
        Assert.Equal(Key, AIOptions.Clean(Key));
        Assert.Equal("qwen/qwen3.8-27b", AIOptions.Clean("qwen/qwen3.8-27b"));
        Assert.Equal(
            "https://api.groq.com/openai/v1",
            AIOptions.Clean("https://api.groq.com/openai/v1"));
    }

    [Fact]
    public void One_model_serves_both_operations_unless_told_otherwise()
    {
        // The ordinary case, and the one that must not need configuring.
        var options = new AIOptions { Model = "openai/gpt-oss-120b" };

        Assert.Equal("openai/gpt-oss-120b", options.ModelForRanking);
        Assert.Equal("openai/gpt-oss-120b", options.ModelForExtraction);
    }

    [Fact]
    public void A_named_model_takes_over_its_own_operation_only()
    {
        // Why this exists: the best model for ranking on this account cannot extract at all, so
        // one setting for both meant accepting a worse answer on one side.
        var options = new AIOptions
        {
            Model = "openai/gpt-oss-120b",
            RankingModel = "qwen/qwen3.8-27b",
        };

        Assert.Equal("qwen/qwen3.8-27b", options.ModelForRanking);
        Assert.Equal("openai/gpt-oss-120b", options.ModelForExtraction);
    }

    [Fact]
    public void Both_can_be_named_and_neither_falls_back()
    {
        var options = new AIOptions
        {
            Model = "unused",
            RankingModel = "qwen/qwen3.8-27b",
            ExtractionModel = "openai/gpt-oss-120b",
        };

        Assert.Equal("qwen/qwen3.8-27b", options.ModelForRanking);
        Assert.Equal("openai/gpt-oss-120b", options.ModelForExtraction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_not_a_model_and_falls_back(string named)
    {
        // A variable set to nothing - which is what an unset compose passthrough produces - has
        // to mean "not specified" rather than "use the empty model".
        var options = new AIOptions { Model = "openai/gpt-oss-120b", RankingModel = named };

        Assert.Equal("openai/gpt-oss-120b", options.ModelForRanking);
    }

    [Fact]
    public void A_quoted_provider_name_still_selects_its_adapter()
    {
        // The reason Provider is cleaned too. Registration compares this against "anthropic",
        // and a quoted value would fail that equality and silently pick the other adapter.
        Assert.Equal("anthropic", AIOptions.Clean("\"anthropic\""));
    }
}
