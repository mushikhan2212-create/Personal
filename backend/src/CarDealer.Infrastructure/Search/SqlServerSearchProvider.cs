using System.Diagnostics;
using CarDealer.Application.Search;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Infrastructure.Search;

/// <summary>
/// Search over SQL Server, the first <see cref="ISearchProvider"/> (decision D4).
/// </summary>
/// <remarks>
/// Tenant isolation is not implemented here and must not be: the DbContext's global query
/// filters already scope every table this touches, so a listing belongs to the global catalog
/// or to the caller's own tenant and nothing else is reachable. Re-implementing the predicate
/// here would be a second place to get it wrong.
///
/// What this class does own is the overlay - a tenant's own price, and their decision to hide
/// a vehicle from their own search. That is per-tenant commercial state over shared rows, and
/// it is the reason search cannot simply select from Vehicles.
/// </remarks>
public sealed class SqlServerSearchProvider : ISearchProvider
{
    private readonly CarDealerDbContext _db;

    public SqlServerSearchProvider(CarDealerDbContext db) => _db = db;

    public async Task<VehicleSearchResult> SearchAsync(
        VehicleSearchQuery query, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // Listings rather than vehicles: a listing is what carries a price, and one vehicle
        // can have several. The query filters scope both to null-or-mine.
        var listings = _db.VehicleListings
            .AsNoTracking()
            .Where(l => l.IsActive)
            .Where(l => l.Vehicle.Status == VehicleStatus.Active);

        if (query.VehicleSourceId is { } sourceId)
        {
            listings = listings.Where(l => l.VehicleSourceId == sourceId);
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var text = query.Text.Trim();

            // Raw make and model, not the normalized ids: an unmapped alias leaves MakeId null
            // and the vehicle must stay findable by the text it arrived with, or normalization
            // gaps become silent inventory loss (canonical model spec section 7).
            listings = listings.Where(l =>
                (l.Vehicle.Make != null && EF.Functions.Like(l.Vehicle.Make, $"%{text}%"))
                || (l.Vehicle.Model != null && EF.Functions.Like(l.Vehicle.Model, $"%{text}%"))
                || (l.Vehicle.Variant != null && EF.Functions.Like(l.Vehicle.Variant, $"%{text}%")));
        }

        if (query.MakeId is { } makeId) listings = listings.Where(l => l.Vehicle.MakeId == makeId);
        if (query.ModelId is { } modelId) listings = listings.Where(l => l.Vehicle.ModelId == modelId);
        if (query.MinYear is { } minYear) listings = listings.Where(l => l.Vehicle.ModelYear >= minYear);
        if (query.MaxYear is { } maxYear) listings = listings.Where(l => l.Vehicle.ModelYear <= maxYear);
        if (query.MaxMileage is { } maxMileage) listings = listings.Where(l => l.Vehicle.Mileage <= maxMileage);

        if (query.SteeringSide is { } steering) listings = listings.Where(l => l.Vehicle.SteeringSide == steering);
        if (query.FuelType is { } fuel) listings = listings.Where(l => l.Vehicle.FuelType == fuel);
        if (query.Transmission is { } transmission) listings = listings.Where(l => l.Vehicle.Transmission == transmission);

        // Null base price is excluded rather than treated as zero. A listing whose FX was
        // unavailable at sync time is not a free car, and decision D6 is explicit that nulls
        // stay out of base-currency range filters.
        if (query.MinPriceBase is { } minPrice)
        {
            listings = listings.Where(l => l.PriceBaseCurrency != null && l.PriceBaseCurrency >= minPrice);
        }

        if (query.MaxPriceBase is { } maxPrice)
        {
            listings = listings.Where(l => l.PriceBaseCurrency != null && l.PriceBaseCurrency <= maxPrice);
        }

        // The tenant's own overlay. TenantVehicles is filtered to the current tenant by the
        // DbContext, so this can never surface another tenant's price or honour their hiding.
        var overlays = _db.TenantVehicles.AsNoTracking();

        var projected =
            from l in listings
            join o in overlays on l.VehicleId equals o.VehicleId into overlayGroup
            from overlay in overlayGroup.DefaultIfEmpty()
            where overlay == null || !overlay.IsHidden
            select new { Listing = l, Overlay = overlay };

        var total = await projected.CountAsync(ct).ConfigureAwait(false);

        projected = query.Sort switch
        {
            VehicleSearchSort.PriceAscending =>
                projected.OrderBy(x => x.Listing.PriceBaseCurrency ?? decimal.MaxValue),
            VehicleSearchSort.PriceDescending =>
                projected.OrderByDescending(x => x.Listing.PriceBaseCurrency ?? decimal.MinValue),
            VehicleSearchSort.YearDescending =>
                projected.OrderByDescending(x => x.Listing.Vehicle.ModelYear ?? 0),
            VehicleSearchSort.MileageAscending =>
                projected.OrderBy(x => x.Listing.Vehicle.Mileage ?? int.MaxValue),
            _ => projected.OrderByDescending(x => x.Listing.LastSeenAtUtc),
        };

        var hits = await projected
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new VehicleSearchHit
            {
                PublicId = x.Listing.Vehicle.PublicId,
                Make = x.Listing.Vehicle.Make,
                Model = x.Listing.Vehicle.Model,
                Variant = x.Listing.Vehicle.Variant,
                ModelYear = x.Listing.Vehicle.ModelYear,
                Mileage = x.Listing.Vehicle.Mileage,
                MileageUnit = x.Listing.Vehicle.MileageUnit,
                SteeringSide = x.Listing.Vehicle.SteeringSide,
                FuelType = x.Listing.Vehicle.FuelType,
                Transmission = x.Listing.Vehicle.Transmission,
                Price = x.Listing.Price,
                CurrencyCode = x.Listing.CurrencyCode,
                PriceBaseCurrency = x.Listing.PriceBaseCurrency,
                BaseCurrencyCode = x.Listing.BaseCurrencyCode,
                PriceType = x.Listing.PriceType,
                SourceName = x.Listing.VehicleSource.Name,
                SourceUrl = x.Listing.SourceUrl,
                PrimaryImageUrl = x.Listing.Vehicle.Images
                    .OrderBy(i => i.SortOrder)
                    .Select(i => i.ImageUrl)
                    .FirstOrDefault(),
                LastSeenAtUtc = x.Listing.LastSeenAtUtc,
                TenantPrice = x.Overlay != null ? x.Overlay.TenantPrice : null,
                TenantCurrencyCode = x.Overlay != null ? x.Overlay.TenantCurrencyCode : null,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        stopwatch.Stop();

        return new VehicleSearchResult
        {
            Hits = hits,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            Elapsed = stopwatch.Elapsed,
        };
    }
}
