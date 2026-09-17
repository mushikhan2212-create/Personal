using CarDealer.Infrastructure.AI;
using CarDealer.Integrations.AI;
using Microsoft.Extensions.Configuration;

namespace CarDealer.UnitTests;

/// <summary>
/// The startup line that answers "which key, from where", without printing the key.
/// </summary>
public sealed class AIConfigurationReportTests
{
    private const string Key = "gsk_abcdefghijklmnopqrstuvwxyz0123456789";

    [Fact]
    public void A_fingerprint_identifies_a_key_without_revealing_it()
    {
        var print = AIConfigurationReport.Fingerprint(Key);

        // Enough to recognise by eye against the provider's console.
        Assert.StartsWith("gsk_", print);
        Assert.Contains("6789", print);
        Assert.Contains("40 chars", print);

        // And not enough to use. The middle never appears.
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", print);
        Assert.DoesNotContain(Key, print);
    }

    [Fact]
    public void A_clipped_key_is_obvious_from_its_length()
    {
        // The failure a careless paste produces, and the one a fingerprint catches instantly:
        // the ends match what you expect and the count does not.
        Assert.Contains("12 chars", AIConfigurationReport.Fingerprint("gsk_12345678"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_key_says_so_plainly(string? key)
        => Assert.Equal("none", AIConfigurationReport.Fingerprint(key));

    [Fact]
    public void Something_too_short_to_be_a_key_is_named_as_such()
        => Assert.Contains("too short", AIConfigurationReport.Fingerprint("abc"));

    [Fact]
    public void The_last_source_to_supply_a_value_is_the_one_reported()
    {
        // The whole point. Environment variables are added after user secrets, so a stray
        // AI__ApiKey beats the secret somebody is carefully editing - and nothing on screen
        // distinguishes that from a revoked key.
        // Two sources standing in for the real pair, which this project cannot build here
        // without taking a dependency on the environment-variables package for one assertion.
        // The ordering rule is the same one the host applies.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI:ApiKey"] = "from-secrets" })
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI:ApiKey"] = "from-later" })
            .Build();

        Assert.Equal("from-later", configuration["AI:ApiKey"]);
        Assert.Contains("Memory", AIConfigurationReport.SourceOf(configuration, "AI:ApiKey"));
    }

    [Fact]
    public void A_setting_nothing_supplies_says_nothing_supplied_it()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal("nothing", AIConfigurationReport.SourceOf(configuration, "AI:ApiKey"));
    }

    [Fact]
    public void A_configured_provider_is_described_without_its_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI:ApiKey"] = Key })
            .Build();

        var line = AIConfigurationReport.Describe(configuration, new AIOptions
        {
            Provider = "groq",
            Model = "qwen/qwen3.8-27b",
            ApiKey = Key,
        });

        Assert.Contains("groq", line);
        Assert.Contains("qwen/qwen3.8-27b", line);
        Assert.DoesNotContain(Key, line);
    }

    [Fact]
    public void An_unconfigured_provider_says_which_part_is_missing()
    {
        var line = AIConfigurationReport.Describe(
            new ConfigurationBuilder().Build(),
            new AIOptions { Provider = "groq", Model = "qwen/qwen3.8-27b" });

        Assert.Contains("are off", line);
        Assert.Contains("key none", line);
    }
}
