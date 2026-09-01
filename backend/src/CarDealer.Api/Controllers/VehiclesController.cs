using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Search;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace CarDealer.Api.Controllers;

/// <summary>
/// The vehicle catalog. Under decision D10 this OpenAPI document is the contract the Phase 0.5
/// frontend generates its types from, so every response shape here is a published interface.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/vehicles")]
public sealed class VehiclesController : ControllerBase
{
    private readonly ISearchProvider _search;

    public VehiclesController(ISearchProvider search) => _search = search;

    /// <summary>Searches the catalog visible to the caller's tenant.</summary>
    /// <remarks>
    /// Visibility is not a parameter. The result is the global catalog plus this tenant's own
    /// private inventory, minus anything this tenant has hidden - all of it decided by the
    /// authenticated token, never by the request (acceptance criterion C2).
    ///
    /// Prices carry their incoterm because comparing an FOB figure against a CIF one compares
    /// two different numbers, and a price with no stated incoterm reads Unknown rather than
    /// being quietly assumed.
    /// </remarks>
    [HttpGet]
    [HasPermission(Permissions.VehiclesRead)]
    [ProducesResponseType(typeof(VehicleSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Search([FromQuery] VehicleSearchRequest request, CancellationToken ct)
    {
        if (request.MinYear is { } min && request.MaxYear is { } max && min > max)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "minYear cannot be greater than maxYear.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (request.MinPrice is { } minPrice && request.MaxPrice is { } maxPrice && minPrice > maxPrice)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "minPrice cannot be greater than maxPrice.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var result = await _search.SearchAsync(
            new VehicleSearchQuery
            {
                Text = request.Q,
                MinYear = request.MinYear,
                MaxYear = request.MaxYear,
                MaxMileage = request.MaxMileage,
                SteeringSide = request.SteeringSide,
                FuelType = request.FuelType,
                Transmission = request.Transmission,
                MinPriceBase = request.MinPrice,
                MaxPriceBase = request.MaxPrice,
                Page = request.Page,
                PageSize = request.PageSize,
                Sort = request.Sort,
            },
            ct).ConfigureAwait(false);

        return Ok(new VehicleSearchResponse
        {
            Items = [.. result.Hits.Select(VehicleSummary.From)],
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
            TotalPages = result.PageSize == 0 ? 0 : (int)Math.Ceiling(result.TotalCount / (double)result.PageSize),

            // Returned rather than only logged: master prompt section 8 makes response time a
            // measured POC criterion, and the screen is where the measuring happens.
            ElapsedMilliseconds = (int)result.Elapsed.TotalMilliseconds,
        });
    }
}

/// <summary>Query parameters for a catalog search.</summary>
public sealed record VehicleSearchRequest
{
    /// <summary>Free text over make, model and variant.</summary>
    public string? Q { get; init; }

    public int? MinYear { get; init; }

    public int? MaxYear { get; init; }

    public int? MaxMileage { get; init; }

    /// <summary>The most-used filter in the export trade (decision D5).</summary>
    public SteeringSide? SteeringSide { get; init; }

    public FuelType? FuelType { get; init; }

    public Transmission? Transmission { get; init; }

    /// <summary>
    /// Range over the normalized base price. Listings whose base price is unknown are excluded
    /// from the range rather than treated as zero (decision D6).
    /// </summary>
    public decimal? MinPrice { get; init; }

    public decimal? MaxPrice { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 24;

    public VehicleSearchSort Sort { get; init; } = VehicleSearchSort.RecentlySeen;
}

public sealed record VehicleSearchResponse
{
    public required IReadOnlyList<VehicleSummary> Items { get; init; }

    public int TotalCount { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalPages { get; init; }

    /// <summary>Server-side query time, for the POC's p95 measurement.</summary>
    public int ElapsedMilliseconds { get; init; }
}

/// <summary>One row of the vehicle grid.</summary>
public sealed record VehicleSummary
{
    public required Guid Id { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public int? Year { get; init; }

    public int? Mileage { get; init; }

    /// <summary>Never compare mileage across units without normalizing first.</summary>
    public string MileageUnit { get; init; } = nameof(Domain.Enums.MileageUnit.Unknown);

    public string SteeringSide { get; init; } = nameof(Domain.Enums.SteeringSide.Unknown);

    public string FuelType { get; init; } = nameof(Domain.Enums.FuelType.Unknown);

    public string Transmission { get; init; } = nameof(Domain.Enums.Transmission.Unknown);

    public decimal? Price { get; init; }

    public string? CurrencyCode { get; init; }

    public decimal? PriceBaseCurrency { get; init; }

    public string? BaseCurrencyCode { get; init; }

    /// <summary>
    /// The incoterm the price is quoted under, as a name rather than a number so the contract
    /// survives a renumbering.
    /// </summary>
    public string PriceType { get; init; } = nameof(Domain.Enums.PriceType.Unknown);

    /// <summary>Where the listing came from. Attribution is a POC acceptance criterion.</summary>
    public string? SourceName { get; init; }

    public string? SourceUrl { get; init; }

    public string? ImageUrl { get; init; }

    public DateTime LastSeenAtUtc { get; init; }

    /// <summary>This tenant's own price, when they have set one. Never another tenant's.</summary>
    public decimal? TenantPrice { get; init; }

    public string? TenantCurrencyCode { get; init; }

    public static VehicleSummary From(VehicleSearchHit hit) => new()
    {
        Id = hit.PublicId,
        Make = hit.Make,
        Model = hit.Model,
        Variant = hit.Variant,
        Year = hit.ModelYear,
        Mileage = hit.Mileage,
        MileageUnit = hit.MileageUnit.ToString(),
        SteeringSide = hit.SteeringSide.ToString(),
        FuelType = hit.FuelType.ToString(),
        Transmission = hit.Transmission.ToString(),
        Price = hit.Price,
        CurrencyCode = hit.CurrencyCode,
        PriceBaseCurrency = hit.PriceBaseCurrency,
        BaseCurrencyCode = hit.BaseCurrencyCode,
        PriceType = hit.PriceType.ToString(),
        SourceName = hit.SourceName,
        SourceUrl = hit.SourceUrl,
        ImageUrl = hit.PrimaryImageUrl,
        LastSeenAtUtc = hit.LastSeenAtUtc,
        TenantPrice = hit.TenantPrice,
        TenantCurrencyCode = hit.TenantCurrencyCode,
    };
}
