namespace CarDealer.Application.AI;

/// <summary>
/// What a customer is looking for, with the customer removed.
/// </summary>
/// <remarks>
/// <para>
/// This type is the enforcement point for open item O4, and its shape is the whole argument.
/// Ranking stock against a requirement needs to know that somebody wants a 2016-or-newer
/// right-hand-drive Corolla under seven thousand dollars. It does not need to know who they
/// are. So there is no field here for a name, a phone number, an email address or a note -
/// not "we don't populate them", but nowhere to put them.
/// </para>
///
/// <para>
/// <c>CustomerRequirement.RawRequirementText</c> is deliberately absent for the same reason.
/// It is free text a salesperson typed, which is exactly where a phone number or a shipping
/// address ends up in this trade, and free text is the half of O4 nobody has answered. A
/// structured filter is not personal data; the sentence it was derived from might be.
/// </para>
///
/// <para>
/// The practical consequence is that this feature ships without waiting on that decision. If
/// somebody later adds a field here, they are making a data-protection choice, and the fact
/// that they had to edit this file is the point.
/// </para>
/// </remarks>
public sealed record RequirementBrief
{
    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public string? BodyType { get; init; }

    public int? MinYear { get; init; }

    public int? MaxYear { get; init; }

    public int? MinMileage { get; init; }

    public int? MaxMileage { get; init; }

    /// <summary>Already in the trade's words, never an enum name.</summary>
    public string? Transmission { get; init; }

    public string? FuelType { get; init; }

    public decimal? MinPrice { get; init; }

    public decimal? MaxPrice { get; init; }

    /// <summary>ISO 3166-1 alpha-2. Where the car is going, which bears on steering side.</summary>
    public string? DestinationCountryCode { get; init; }

    /// <summary>Every number this brief states, for grounding the model's reasons against.</summary>
    public IEnumerable<decimal> Numbers()
    {
        if (MinYear is { } a) yield return a;
        if (MaxYear is { } b) yield return b;
        if (MinMileage is { } c) yield return c;
        if (MaxMileage is { } d) yield return d;
        if (MinPrice is { } e) yield return e;
        if (MaxPrice is { } f) yield return f;
    }
}

/// <summary>
/// One car offered to the ranker, as the ranker sees it.
/// </summary>
/// <remarks>
/// A projection rather than the entity, for the same reason the search hit is one: what goes
/// over the wire should be exactly what the task needs, so that adding a field is a decision
/// somebody makes rather than a side effect of loading a row.
/// </remarks>
public sealed record CandidateVehicle
{
    public required Guid Id { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public int? Year { get; init; }

    public int? Mileage { get; init; }

    public string? MileageUnit { get; init; }

    public string? Colour { get; init; }

    public string? BodyType { get; init; }

    public string? Fuel { get; init; }

    public string? Transmission { get; init; }

    public string? Steering { get; init; }

    public int? EngineCc { get; init; }

    /// <summary>The exporter's asking price, in the comparable base currency.</summary>
    public decimal? SourcePrice { get; init; }

    public string? SourceCurrency { get; init; }

    /// <summary>The dealer's own retail price, when they have set one.</summary>
    /// <remarks>
    /// Sent because it is what the customer would actually pay, so it is the number a budget
    /// should be judged against. Safe to send and safe to quote back: the reasons are read by
    /// the salesperson, who edits every message before it goes.
    /// </remarks>
    public decimal? RetailPrice { get; init; }

    public string? RetailCurrency { get; init; }

    /// <summary>How many sources offer this car - evidence of availability, not of quality.</summary>
    public int OfferCount { get; init; } = 1;

    /// <summary>Every number this car states, for grounding the model's reasons against.</summary>
    public IEnumerable<decimal> Numbers()
    {
        if (Year is { } a) yield return a;
        if (Mileage is { } b) yield return b;
        if (EngineCc is { } c) yield return c;
        if (SourcePrice is { } d) yield return decimal.Round(d);
        if (RetailPrice is { } e) yield return decimal.Round(e);
        yield return OfferCount;
    }
}

/// <summary>Everything the ranker is given for one call.</summary>
public sealed record RankingRequest
{
    public required RequirementBrief Requirement { get; init; }

    public required IReadOnlyList<CandidateVehicle> Candidates { get; init; }
}

/// <summary>One car's place in the ranking, as the model returned it.</summary>
public sealed record RankedVehicle
{
    public required Guid Id { get; init; }

    /// <summary>1 is best. Contiguous from 1 across the whole candidate set.</summary>
    public required int Rank { get; init; }

    /// <summary>How well it fits, 0 to 1.</summary>
    public required decimal Score { get; init; }

    /// <summary>Short phrases a salesperson can read at a glance.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>What a call cost, as the provider reported it.</summary>
public sealed record AIUsage
{
    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    /// <summary>Null when the provider does not report a price for the call.</summary>
    public decimal? CostUsd { get; init; }
}

/// <summary>The outcome of asking a provider to rank a candidate set.</summary>
/// <remarks>
/// A result rather than an exception, because every failure here has the same answer - show
/// the deterministic order and say why - and modelling that as a thrown exception would make
/// the ordinary case look exceptional.
/// </remarks>
public sealed record AIRankingResult
{
    public bool Succeeded => Ranked is not null;

    /// <summary>The ranking, or null when the call failed or was refused.</summary>
    public IReadOnlyList<RankedVehicle>? Ranked { get; init; }

    /// <summary>Why there is no ranking, in words a salesperson can act on.</summary>
    public string? Failure { get; init; }

    public AIUsage? Usage { get; init; }

    /// <summary>Which provider and model answered, for the audit row.</summary>
    public string? Provider { get; init; }

    public string? Model { get; init; }

    public static AIRankingResult Failed(string why, string? provider = null, string? model = null)
        => new() { Failure = why, Provider = provider, Model = model };
}
