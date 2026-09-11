using System.Globalization;
using System.Text.RegularExpressions;

namespace CarDealer.Integrations.AI;

/// <summary>
/// Turns a provider's refusal into something the person reading it can act on.
/// </summary>
/// <remarks>
/// Only the rate limit is treated specially, because it is the only refusal here that a
/// salesperson will meet regularly and can do nothing at all with in its raw form: four hundred
/// characters of JSON, truncated mid-sentence inside an upgrade URL, with the two numbers that
/// matter buried in the middle of it. Everything else stays raw on purpose - an unknown model id
/// and a decommissioned one both arrive as a 400 whose body is the only thing that tells them
/// apart, and paraphrasing that would cost a diagnosis to save a line.
/// </remarks>
public static partial class ProviderErrors
{
    [GeneratedRegex(@"Limit\s+(\d[\d,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex LimitPattern();

    [GeneratedRegex(@"Requested\s+(\d[\d,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex RequestedPattern();

    /// <summary>
    /// A 429 in words, with the fix.
    /// </summary>
    /// <remarks>
    /// The advice is specific because the arithmetic is not obvious and gets people twice.
    /// <c>max_tokens</c> is what a request <i>asks</i> for, and a value above the whole
    /// per-minute allowance is refused however short the answer would have been - so a ceiling
    /// set above the limit can never succeed, on any requirement. Lowering it below the limit
    /// removes the refusal outright; whether the answer then fits is a question about how many
    /// cars are being sent, which is the second half of the advice.
    /// </remarks>
    public static string RateLimit(string provider, string detail)
    {
        var limit = Figure(LimitPattern(), detail);
        var requested = Figure(RequestedPattern(), detail);

        if (limit is null || requested is null)
        {
            return $"{provider} refused the request: the account's rate limit was reached. "
                + "Wait a minute and try again, or send fewer cars with AI__MaxCandidates.";
        }

        return $"{provider} refused the request: this account allows {limit:N0} output tokens a "
            + $"minute, and the request asked for {requested:N0}. Set AI__MaxTokens below "
            + $"{limit:N0}, send fewer cars with AI__MaxCandidates, or raise the account's tier.";
    }

    private static int? Figure(Regex pattern, string text)
    {
        var match = pattern.Match(text);

        return match.Success
            && int.TryParse(
                match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var value)
            ? value
            : null;
    }
}
