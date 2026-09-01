namespace CarDealer.Application.VehicleSources;

/// <summary>
/// A request for one page of listings, in terms every source can understand.
/// </summary>
/// <remarks>
/// Deliberately smaller than the filter set any one provider supports. A provider translates
/// what it can and ignores what it cannot; putting every provider's filters here would make
/// the abstraction a union of vendor quirks, which is the thing it exists to prevent.
/// </remarks>
public sealed record VehicleSourceQuery
{
    /// <summary>
    /// The sub-source to draw from - `sbtjapan`, `goonet_exchange`. Required by decision D12:
    /// no unfiltered call is ever made.
    /// </summary>
    public required string SourceCode { get; init; }

    public int Page { get; init; } = 1;

    /// <summary>
    /// Larger pages mean fewer requests for the same coverage. This is a quota control, not a
    /// rate limit - the two are separate, and the provider still needs backoff.
    /// </summary>
    public int PageSize { get; init; } = 100;

    public string? Make { get; init; }

    public string? Model { get; init; }

    public int? MinYear { get; init; }

    public int? MaxYear { get; init; }

    public int? MaxMileage { get; init; }

    /// <summary>Restrict to listings the source still considers available.</summary>
    public bool AvailableOnly { get; init; } = true;
}

/// <summary>One page of raw records, plus what the source said about the rest.</summary>
public sealed record VehicleSourcePage
{
    public required IReadOnlyList<RawVehicleRecord> Records { get; init; }

    /// <summary>Total matching the query, as reported by the source.</summary>
    public int TotalCount { get; init; }

    public int Page { get; init; }

    public int TotalPages { get; init; }

    public bool HasNextPage { get; init; }

    /// <summary>Wall-clock time the request took, for the POC's response-time measurement.</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// One record exactly as the source returned it, before any interpretation.
/// </summary>
/// <remarks>
/// The raw payload is carried alongside the identifier rather than discarded, because SQL
/// schema spec section 8 requires source records be preserved for debugging and reprocessing.
/// Normalization can then be re-run over stored payloads without re-fetching - which matters
/// when the mapping is as provisional as this one.
/// </remarks>
public sealed record RawVehicleRecord
{
    /// <summary>The source's own identifier for this record.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Which sub-source produced it, for attribution.</summary>
    public required string SourceCode { get; init; }

    /// <summary>The payload verbatim. Never parsed by callers - that is the normalizer's job.</summary>
    public required string RawPayload { get; init; }

    public DateTime RetrievedAtUtc { get; init; }
}

/// <summary>What a provider reports about one of its sub-sources.</summary>
public sealed record VehicleSourceDescriptor
{
    public required string Code { get; init; }

    public required string Name { get; init; }

    public string? Region { get; init; }

    public string? Country { get; init; }

    /// <summary>
    /// The provider's own availability word, unmapped. Carapis says `live` or `on_demand`, and
    /// neither predicts whether the source carries data - only a count does - so this is
    /// recorded rather than acted on.
    /// </summary>
    public string? Availability { get; init; }

    public DateTime? LastParsedAtUtc { get; init; }
}
