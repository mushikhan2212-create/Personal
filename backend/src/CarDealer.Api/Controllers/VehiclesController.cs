using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Search;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    private readonly CarDealerDbContext _db;

    public VehiclesController(ISearchProvider search, CarDealerDbContext db)
    {
        _search = search;
        _db = db;
    }

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

    /// <summary>One vehicle in full, with every image and every listing offering it.</summary>
    /// <remarks>
    /// Every listing, not just the cheapest: one physical car can be offered by several
    /// sources at different prices under different incoterms, and showing them together is
    /// what makes a deduplication decision auditable by the person looking at it. A merge that
    /// cannot be inspected is a merge nobody can trust.
    ///
    /// Visibility comes from the DbContext's query filters, exactly as search does. A vehicle
    /// belonging to another tenant is simply not found - 404, not 403, because confirming that
    /// an id exists would leak the shape of another tenant's inventory.
    /// </remarks>
    [HttpGet("{publicId:guid}")]
    [HasPermission(Permissions.VehiclesRead)]
    [ProducesResponseType(typeof(VehicleDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var vehicle = await _db.Vehicles
            .AsNoTracking()
            .Include(v => v.Images)
            .Include(v => v.Listings).ThenInclude(l => l.VehicleSource)
            .FirstOrDefaultAsync(v => v.PublicId == publicId, ct)
            .ConfigureAwait(false);

        if (vehicle is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No such vehicle.",
                Status = StatusCodes.Status404NotFound,
            });
        }

        var overlay = await _db.TenantVehicles
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.VehicleId == vehicle.Id, ct)
            .ConfigureAwait(false);

        return Ok(VehicleDetail.From(vehicle, overlay));
    }

}

/// <summary>One vehicle, in full.</summary>
public sealed record VehicleDetail
{
    public required Guid Id { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public int? Year { get; init; }

    public string? BodyType { get; init; }

    public int? EngineDisplacementCc { get; init; }

    public int? Mileage { get; init; }

    public string MileageUnit { get; init; } = nameof(Domain.Enums.MileageUnit.Unknown);

    public string SteeringSide { get; init; } = nameof(Domain.Enums.SteeringSide.Unknown);

    public string FuelType { get; init; } = nameof(Domain.Enums.FuelType.Unknown);

    public string Transmission { get; init; } = nameof(Domain.Enums.Transmission.Unknown);

    public string Drivetrain { get; init; } = nameof(Domain.Enums.Drivetrain.Unknown);

    public string Status { get; init; } = nameof(VehicleStatus.Unknown);

    public string? ExteriorColor { get; init; }

    public string? InteriorColor { get; init; }

    public string? Condition { get; init; }

    public string? AuctionGrade { get; init; }

    /// <summary>
    /// The identifiers, and which one identity was built from.
    /// </summary>
    /// <remarks>
    /// Exposed because "why did these two listings merge, or why did they not?" is the
    /// question this phase exists to answer honestly. Most Japanese stock has a chassis number
    /// and no VIN, and some has neither - in which case nothing can be merged on and the
    /// screen should say so rather than implying a judgement was made.
    /// </remarks>
    public string? Vin { get; init; }

    public string? ChassisNumber { get; init; }

    public string? LotNumber { get; init; }

    public string? CanonicalHashSource { get; init; }

    public required IReadOnlyList<string> ImageUrls { get; init; }

    public required IReadOnlyList<VehicleDetailListing> Listings { get; init; }

    public decimal? TenantPrice { get; init; }

    public string? TenantCurrencyCode { get; init; }

    public string? InternalNotes { get; init; }

    public static VehicleDetail From(Vehicle vehicle, TenantVehicle? overlay) => new()
    {
        Id = vehicle.PublicId,
        Make = vehicle.Make,
        Model = vehicle.Model,
        Variant = vehicle.Variant,
        Year = vehicle.ModelYear,
        BodyType = vehicle.BodyType,
        EngineDisplacementCc = vehicle.EngineDisplacementCc,
        Mileage = vehicle.Mileage,
        MileageUnit = vehicle.MileageUnit.ToString(),
        SteeringSide = vehicle.SteeringSide.ToString(),
        FuelType = vehicle.FuelType.ToString(),
        Transmission = vehicle.Transmission.ToString(),
        Drivetrain = vehicle.Drivetrain.ToString(),
        Status = vehicle.Status.ToString(),
        ExteriorColor = vehicle.ExteriorColor,
        InteriorColor = vehicle.InteriorColor,
        Condition = vehicle.Condition,
        AuctionGrade = vehicle.AuctionGrade,
        Vin = vehicle.Vin,
        ChassisNumber = vehicle.ChassisNumber,
        LotNumber = vehicle.LotNumber,
        CanonicalHashSource = vehicle.CanonicalHashSource?.ToString(),
        ImageUrls = [.. vehicle.Images.OrderBy(i => i.SortOrder).Select(i => i.ImageUrl)],
        Listings =
        [
            .. vehicle.Listings
                .OrderByDescending(l => l.LastSeenAtUtc)
                .Select(VehicleDetailListing.From),
        ],
        TenantPrice = overlay?.TenantPrice,
        TenantCurrencyCode = overlay?.TenantCurrencyCode,
        InternalNotes = overlay?.InternalNotes,
    };
}

/// <summary>One source's offer of a vehicle.</summary>
public sealed record VehicleDetailListing
{
    public string? SourceName { get; init; }

    public string? SourceUrl { get; init; }

    public string? ExternalListingId { get; init; }

    public decimal? Price { get; init; }

    public string? CurrencyCode { get; init; }

    public decimal? PriceBaseCurrency { get; init; }

    public string? BaseCurrencyCode { get; init; }

    public string PriceType { get; init; } = nameof(Domain.Enums.PriceType.Unknown);

    public string? PortOfLoading { get; init; }

    public string? LocationCountryCode { get; init; }

    public bool IsActive { get; init; }

    public DateTime FirstSeenAtUtc { get; init; }

    public DateTime LastSeenAtUtc { get; init; }

    public static VehicleDetailListing From(VehicleListing listing) => new()
    {
        SourceName = listing.VehicleSource?.Name,
        SourceUrl = listing.SourceUrl,
        ExternalListingId = listing.ExternalListingId,
        Price = listing.Price,
        CurrencyCode = listing.CurrencyCode,
        PriceBaseCurrency = listing.PriceBaseCurrency,
        BaseCurrencyCode = listing.BaseCurrencyCode,
        PriceType = listing.PriceType.ToString(),
        PortOfLoading = listing.PortOfLoading,
        LocationCountryCode = listing.LocationCountryCode,
        IsActive = listing.IsActive,
        FirstSeenAtUtc = listing.FirstSeenAtUtc,
        LastSeenAtUtc = listing.LastSeenAtUtc,
    };
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

    /// <summary>Listings offering this car, and how many distinct sources they come from.</summary>
    public int OfferCount { get; init; } = 1;

    public int SourceCount { get; init; } = 1;

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
        OfferCount = hit.OfferCount,
        SourceCount = hit.SourceCount,
        TenantPrice = hit.TenantPrice,
        TenantCurrencyCode = hit.TenantCurrencyCode,
    };
}
