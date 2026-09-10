using System.Globalization;
using System.Text.RegularExpressions;

namespace CarDealer.Application.AI;

/// <summary>
/// The checks a model's ranking has to pass before anybody sees it.
/// </summary>
/// <remarks>
/// <para>
/// Master prompt section 11 says the platform must never invent availability, price,
/// specifications or customer facts. These are how that is enforced rather than requested: a
/// prompt asks, a guard decides. Every one of them is a pure function over the request and the
/// response, so they are testable without a network call and cost nothing to run.
/// </para>
///
/// <para>
/// The design intent is that model quality becomes a <b>measurable</b> quantity rather than a
/// bet. A weaker model does not produce wrong recommendations here - it produces more
/// rejections, and a rejection falls back to the deterministic order. So swapping providers is
/// something you can compare with a number (how often did the guards fire?) instead of an
/// argument about which is better.
/// </para>
/// </remarks>
public static partial class RankingGuards
{
    /// <summary>
    /// Numbers below this are not checked for grounding.
    /// </summary>
    /// <remarks>
    /// A reason legitimately says "one of only 3 under budget" or "5% cheaper", and neither 3
    /// nor 5 belongs to any car. Years, mileages, prices and engine sizes - the figures that
    /// would actually mislead somebody - are all comfortably above this. Set it lower and the
    /// guard fires on honest sentences; set it higher and a wrong year slips through.
    /// </remarks>
    private const int SmallestCheckedNumber = 100;

    /// <summary>Digits with optional thousands separators and decimals.</summary>
    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")]
    private static partial Regex NumberInText();

    /// <summary>
    /// Checks a ranking against the request that produced it.
    /// </summary>
    /// <returns>Null when the ranking is sound, otherwise why it was rejected.</returns>
    public static string? Reject(RankingRequest request, IReadOnlyList<RankedVehicle> ranked)
    {
        var sent = request.Candidates.ToDictionary(c => c.Id);

        // 1. Nothing invented. An id we did not send is a car the model made up, and a made-up
        //    car with a plausible description is the single worst thing this feature could put
        //    in front of a salesperson about to quote it.
        var invented = ranked.Select(r => r.Id).Where(id => !sent.ContainsKey(id)).ToList();

        if (invented.Count > 0)
        {
            return $"The ranking named {invented.Count} vehicle(s) that were never sent to it.";
        }

        // 2. No duplicates. The same car twice means the ranks are not an ordering.
        //
        //    Checked before the missing-candidate rule because a duplicate usually causes one
        //    too: [A, A] both repeats A and omits B. Both statements are true, and "you listed
        //    the same car twice" is the one that names what actually went wrong.
        var returned = ranked.Select(r => r.Id).ToHashSet();

        if (returned.Count != ranked.Count)
        {
            return "The ranking listed the same vehicle more than once.";
        }

        // 3. Nothing dropped. Omitting candidates is a filter, and filtering is the
        //    deterministic layer's job - a model quietly removing cars has applied a rule
        //    nobody authorised and nobody can see.
        var missing = sent.Keys.Where(id => !returned.Contains(id)).ToList();

        if (missing.Count > 0)
        {
            return $"The ranking left out {missing.Count} of the {sent.Count} vehicles sent to it.";
        }

        // 4. Ranks are a contiguous ordering from 1. Anything else and "rank 3 of 20" is not a
        //    statement about position.
        var ranks = ranked.Select(r => r.Rank).OrderBy(r => r).ToList();

        if (ranks.Where((r, i) => r != i + 1).Any())
        {
            return "The ranks are not a contiguous ordering starting at 1.";
        }

        foreach (var entry in ranked)
        {
            if (entry.Score < 0m || entry.Score > 1m)
            {
                return $"A score of {entry.Score} is outside the range 0 to 1.";
            }

            // 5. Every number in the prose is one the car or the requirement actually stated.
            //    This is the check that stops "48,000 km" appearing beside a car with 62,620 on
            //    the clock - a sentence that reads perfectly and is false.
            // Lambda rather than the decimal.Round method group: it has a (decimal, int)
            // overload, so the group is ambiguous against Select's indexed form.
            var grounded = sent[entry.Id].Numbers()
                .Concat(request.Requirement.Numbers())
                .Select(n => decimal.Round(n))
                .ToHashSet();

            foreach (var reason in entry.Reasons)
            {
                if (Ungrounded(reason, grounded) is { } bad)
                {
                    return $"A reason quotes {bad}, which is not a figure this car or the "
                        + "requirement stated.";
                }
            }
        }

        return null;
    }

    /// <summary>The first number in <paramref name="text"/> that nothing backs up.</summary>
    private static string? Ungrounded(string text, IReadOnlySet<decimal> grounded)
    {
        foreach (Match match in NumberInText().Matches(text))
        {
            // Thousands separators stripped, because the model writes "62,620" for the 62620
            // the database holds and those are the same claim.
            var cleaned = match.Value.Replace(",", string.Empty, StringComparison.Ordinal);

            if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            if (value < SmallestCheckedNumber)
            {
                continue;
            }

            if (!grounded.Contains(decimal.Round(value)))
            {
                return match.Value;
            }
        }

        return null;
    }
}
