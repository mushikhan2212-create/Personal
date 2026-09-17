using CarDealer.Integrations.AI;
using Microsoft.Extensions.Configuration;

namespace CarDealer.Infrastructure.AI;

/// <summary>
/// Says what AI configuration the application actually loaded, and where it came from.
/// </summary>
/// <remarks>
/// <para>
/// Written after a rejected key cost an evening. Every guess was reasonable and none was
/// checkable: the file looked right, the key looked right, and the only evidence was a provider
/// saying "Invalid API Key" about a value nobody could see. The question that settles it is not
/// "what is in the file" but "what did the process load, and which source won" - and those are
/// different questions the moment two sources disagree.
/// </para>
///
/// <para>
/// They disagree more easily here than anywhere else in this system. The default configuration
/// order puts environment variables <b>after</b> user secrets, so one stray <c>AI__ApiKey</c>
/// left over from an experiment silently beats the secret somebody is carefully editing, and
/// nothing on screen distinguishes that from a revoked key.
/// </para>
///
/// <para>
/// <b>The key is never logged.</b> A fingerprint is enough to answer "is this the one I pasted?"
/// by eye, and a log file is exactly the sort of place a key should not end up.
/// </para>
/// </remarks>
public static class AIConfigurationReport
{
    /// <summary>
    /// Enough of a key to recognise it, and not enough to use it.
    /// </summary>
    /// <remarks>
    /// Four characters from each end of a key this long leaves it unusable while still letting
    /// somebody compare it against what their provider's console shows. The length is often the
    /// tell on its own: a key clipped by a careless paste is obvious the moment it is counted.
    /// </remarks>
    public static string Fingerprint(string? key)
    {
        var clean = AIOptions.Clean(key);

        if (clean.Length == 0)
        {
            return "none";
        }

        return clean.Length < 12
            ? $"{clean.Length} chars, too short to be a key"
            : $"{clean[..4]}...{clean[^4..]} ({clean.Length} chars)";
    }

    /// <summary>
    /// Which configuration source supplied a setting, of those that offered one.
    /// </summary>
    /// <remarks>
    /// Walked back to front because that is the order the binder resolves in: the last provider
    /// holding a value is the one that wins. Returning the winner rather than the list is
    /// deliberate - when two sources disagree, the only useful fact is which one is in effect.
    /// </remarks>
    public static string SourceOf(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return "unknown";
        }

        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.TryGet(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return provider.ToString() ?? provider.GetType().Name;
            }
        }

        return "nothing";
    }

    /// <summary>One line for the startup log, safe to paste into a bug report.</summary>
    public static string Describe(IConfiguration configuration, AIOptions options)
    {
        if (!options.IsConfigured)
        {
            return "AI ranking and message reading are off: "
                + $"provider \"{options.Provider}\", model \"{options.Model}\", "
                + $"key {Fingerprint(options.ApiKey)}. All three are needed.";
        }

        // Both named when they differ, because a split configuration is exactly the one a
        // reader would otherwise misremember - and "why is extraction using that model" is a
        // question the startup line should already have answered.
        var models = options.ModelForRanking == options.ModelForExtraction
            ? $"model {options.Model}"
            : $"ranking with {options.ModelForRanking}, reading with {options.ModelForExtraction}";

        return $"AI provider {options.Provider}, {models}, "
            + $"key {Fingerprint(options.ApiKey)} from [{SourceOf(configuration, "AI:ApiKey")}], "
            + $"max tokens {options.MaxTokens}, candidates {options.MaxCandidates}.";
    }
}
