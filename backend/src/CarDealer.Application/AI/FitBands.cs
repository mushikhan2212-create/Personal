namespace CarDealer.Application.AI;

/// <summary>
/// Whether a car sits comfortably inside what the customer asked for, or only just satisfies it.
/// </summary>
/// <remarks>
/// <para>
/// The broker's rule: passing a limit is not the same as fitting it. A car just under the
/// mileage ceiling, or at the oldest year they accepted, is a worse fit than one comfortably
/// inside - even when it is cheaper, and even though it passed the filter.
/// </para>
///
/// <para>
/// This is code rather than a sentence in the prompt because the prompt was tried first and
/// did not hold. Three revisions of a rule saying "rank it below" produced orderings identical
/// to the ones without it: the model reads the sentence, agrees with it, and sorts by price
/// anyway. Instruction-following also varies by model - one on the same account ignored the
/// price rule entirely - so a preference the broker chose would have quietly meant different
/// things on different providers, and changed meaning the day the model id changed. Decision
/// D19.
/// </para>
///
/// <para>
/// The model is still <b>told</b> which cars are flagged, because the reason text is the half a
/// person reads and "close to the mileage they asked for" is worth saying. It is simply no
/// longer asked to act on it.
/// </para>
/// </remarks>
public static class FitBands
{
    /// <summary>How near a stated ceiling counts as only just meeting it.</summary>
    /// <remarks>
    /// A tenth of the ceiling: ask for 120,000 km and anything above 108,000 is tight. Chosen
    /// to be predictable rather than tuned - a broker can tell which side of it a car falls on
    /// without doing arithmetic, and a threshold nobody can predict is worse than a slightly
    /// wrong one. Nothing here is sensitive to its exact value: it decides which of two bands a
    /// car is in, and within a band the ordering is untouched.
    /// </remarks>
    public const decimal TightFraction = 0.10m;

    /// <summary>True when this car only just satisfies a limit the customer set.</summary>
    public static bool IsTight(RequirementBrief requirement, CandidateVehicle candidate)
    {
        // Mileage they named a ceiling for, and this car is in the top tenth of it.
        if (requirement.MaxMileage is { } ceiling
            && ceiling > 0
            && candidate.Mileage is { } mileage
            && mileage > ceiling - (ceiling * TightFraction))
        {
            return true;
        }

        // Exactly the oldest year they were willing to take. Not a band, because years are
        // already coarse - one step older is the whole of the customer's tolerance.
        if (requirement.MinYear is { } oldest && candidate.Year == oldest)
        {
            return true;
        }

        // MaxYear and MaxPrice are deliberately not here. A car at the newest year they accepted
        // is the most desirable end of the range, not the worst; and being near the top of the
        // budget is already paid for by the cheapest-first ordering, so counting it again would
        // demote expensive cars twice and collapse the whole thing back into price order.
        //
        // A missing figure does not flag either. The catalog filter drops a car with no mileage
        // when a ceiling was set, so this cannot arise through the matches path - and where it
        // could, demoting a car for a fact nobody recorded is a guess, which is the one thing
        // this feature is built not to do.
        return false;
    }

    /// <summary>The same candidate, with the flag set if it earns one.</summary>
    /// <remarks>
    /// Left null rather than written as false, so a comfortable car carries no field at all.
    /// The payload drops nulls, and on a twenty-car request that is twenty lines of
    /// "closeToTheirLimits: false" the model has to read past.
    /// </remarks>
    public static CandidateVehicle Flag(RequirementBrief requirement, CandidateVehicle candidate)
        => IsTight(requirement, candidate)
            ? candidate with { CloseToTheirLimits = true }
            : candidate;

    /// <summary>The same request, with every candidate flagged.</summary>
    public static RankingRequest Flag(RankingRequest request)
        => request with
        {
            Candidates = [.. request.Candidates.Select(c => Flag(request.Requirement, c))],
        };

    /// <summary>
    /// Comfortable fits first, and otherwise in the order they arrived.
    /// </summary>
    /// <remarks>
    /// A stable partition, not a sort: LINQ's OrderBy preserves the relative order of equal
    /// keys, so whatever ordering went in survives inside each band. That is the point - this
    /// says which band a car belongs to and nothing else about where it sits.
    /// </remarks>
    public static IEnumerable<T> ComfortableFirst<T>(
        IEnumerable<T> items, Func<T, CandidateVehicle> candidate)
        => items.OrderBy(i => candidate(i).CloseToTheirLimits == true ? 1 : 0);

    /// <summary>
    /// A model's ranking with the limit rule applied and the ranks renumbered.
    /// </summary>
    /// <remarks>
    /// Runs after the guards, on a ranking already checked for invented, dropped and duplicated
    /// cars. Renumbering is safe there and would not be before: renumbering first would repair
    /// exactly the gaps and repeats a guard exists to catch.
    /// </remarks>
    public static IReadOnlyList<RankedVehicle> Apply(
        IReadOnlyList<CandidateVehicle> candidates, IReadOnlyList<RankedVehicle> ranked)
    {
        var tight = candidates
            .Where(c => c.CloseToTheirLimits == true)
            .Select(c => c.Id)
            .ToHashSet();

        // Every car on the same side of every limit, or no limits stated at all. There is
        // nothing to separate, so the model's answer stands untouched.
        if (tight.Count == 0 || tight.Count == candidates.Count)
        {
            return [.. ranked.OrderBy(r => r.Rank)];
        }

        return
        [
            .. ranked
                .OrderBy(r => tight.Contains(r.Id) ? 1 : 0)
                .ThenBy(r => r.Rank)
                .Select((r, i) => r with { Rank = i + 1 }),
        ];
    }
}
