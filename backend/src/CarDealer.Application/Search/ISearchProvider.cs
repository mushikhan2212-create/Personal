using CarDealer.Domain.Enums;

namespace CarDealer.Application.Search;

/// <summary>
/// Vehicle search, behind an abstraction so the engine can change without touching callers
/// (decision D4).
/// </summary>
/// <remarks>
/// SQL Server is the first implementation. Neither specification document states a scale
/// target, so committing to a dedicated search engine now would be a guess; committing to
/// SQL Server forever would be a different guess. The interface buys the option without the
/// infrastructure, and master prompt section 8's response-time measurement is the gate that
/// decides whether to spend it.
///
/// <see cref="VehicleSearchResult.Elapsed"/> exists for exactly that: the POC reports p95
/// latency over the five realistic searches section 8 requires, at whatever catalog size the
/// POC reached, and that number is the evidence for or against adding an engine.
/// </remarks>
public interface ISearchProvider
{
    Task<VehicleSearchResult> SearchAsync(VehicleSearchQuery query, CancellationToken ct = default);
}

/// <summary>A search request, in the canonical model's own terms.</summary>
public sealed record VehicleSearchQuery
{
    /// <summary>Free text over make, model and variant.</summary>
    public string? Text { get; init; }

    public int? MakeId { get; init; }

    public int? ModelId { get; init; }

    public int? MinYear { get; init; }

    public int? MaxYear { get; init; }

    public int? MaxMileage { get; init; }

    public SteeringSide? SteeringSide { get; init; }

    public FuelType? FuelType { get; init; }

    public Transmission? Transmission { get; init; }

    /// <summary>
    /// Range over the normalized base price, which is the only price comparable across
    /// currencies (decision D6). Listings whose base price is null are excluded from a range
    /// filter rather than assumed to be zero.
    /// </summary>
    public decimal? MinPriceBase { get; init; }

    public decimal? MaxPriceBase { get; init; }

    /// <summary>
    /// Restrict to a single source, for the POC's per-source measurements.
    /// </summary>
    public long? VehicleSourceId { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 24;

    public VehicleSearchSort Sort { get; init; } = VehicleSearchSort.RecentlySeen;
}

public enum VehicleSearchSort
{
    RecentlySeen = 0,
    PriceAscending = 1,
    PriceDescending = 2,
    YearDescending = 3,
    MileageAscending = 4,
}

public sealed record VehicleSearchResult
{
    public required IReadOnlyList<VehicleSearchHit> Hits { get; init; }

    public int TotalCount { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; }

    /// <summary>How long the query took. Feeds the POC's p95 measurement (master prompt §8).</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// One result. Deliberately a projection rather than the entity: search returns what a grid
/// renders, not an object graph.
/// </summary>
public sealed record VehicleSearchHit
{
    public required Guid PublicId { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public int? ModelYear { get; init; }

    public int? Mileage { get; init; }

    public MileageUnit MileageUnit { get; init; }

    public SteeringSide SteeringSide { get; init; }

    public FuelType FuelType { get; init; }

    public Transmission Transmission { get; init; }

    public decimal? Price { get; init; }

    public string? CurrencyCode { get; init; }

    public decimal? PriceBaseCurrency { get; init; }

    public string? BaseCurrencyCode { get; init; }

    /// <summary>
    /// Incoterm the price is quoted under. Rendered beside every price, because comparing an
    /// FOB figure with a CIF figure compares two different numbers.
    /// </summary>
    public PriceType PriceType { get; init; }

    /// <summary>Where the listing came from. Master prompt §8 requires attribution be visible.</summary>
    public string? SourceName { get; init; }

    public string? SourceUrl { get; init; }

    public string? PrimaryImageUrl { get; init; }

    public DateTime LastSeenAtUtc { get; init; }

    /// <summary>
    /// The tenant's own price from their overlay, when they have set one. Never another
    /// tenant's - the overlay is strictly tenant-owned.
    /// </summary>
    public decimal? TenantPrice { get; init; }

    public string? TenantCurrencyCode { get; init; }
}
