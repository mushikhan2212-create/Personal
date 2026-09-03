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

    private const string LikeEscape = "\\";

    /// <summary>
    /// Statuses that mean the car is gone. Everything else is searchable.
    /// </summary>
    /// <remarks>
    /// Stated as an exclusion, not as "Status == Active", and that is the whole point.
    ///
    /// Carapis publishes is_available only on the detail endpoint, so a sync that reads the
    /// list - the default, and the cheap one - leaves every vehicle at Unknown, meaning "the
    /// source did not say". Requiring Active turned that silence into a hidden car: a catalog
    /// of 400 synced vehicles returned nothing at all, for every query, with a perfectly
    /// healthy 200. The listing row had already made the opposite call, defaulting IsActive to
    /// true when the field was absent, so the two halves of the same record disagreed about
    /// the same missing value.
    ///
    /// Absence of evidence that a car is gone is not evidence that it is gone. So only the
    /// statuses that positively assert it is gone hide it, and a status this list does not
    /// know about stays visible. That direction matters: a car wrongly shown is a listing
    /// someone clicks and finds sold, while a car wrongly hidden is inventory that silently
    /// does not exist - which is exactly the failure this replaced.
    /// </remarks>
    private static readonly VehicleStatus[] GoneStatuses =
    [
        VehicleStatus.Sold,
        VehicleStatus.Unavailable,
        VehicleStatus.Expired,
        VehicleStatus.Archived,
    ];

    /// <summary>
    /// How many words of a search are honoured. Each one adds an OR group over three columns,
    /// so an unbounded phrase would let a caller build an arbitrarily expensive query.
    /// </summary>
    private const int MaxSearchTerms = 8;

    /// <summary>Splits a search into per-word LIKE patterns, with wildcards escaped.</summary>
    /// <remarks>
    /// Escaping matters: %, _ and [ are wildcards to SQL Server, so a search for "50%" would
    /// otherwise match everything rather than nothing, and the user would have no way to tell
    /// the difference between a broken filter and a popular car.
    /// </remarks>
    private static IEnumerable<string> BuildSearchPatterns(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(MaxSearchTerms)
            .Select(term => "%"
                + term.Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_")
                    .Replace("[", "\\[")
                + "%");

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
            .Where(l => !GoneStatuses.Contains(l.Vehicle.Status));

        if (query.VehicleSourceId is { } sourceId)
        {
            listings = listings.Where(l => l.VehicleSourceId == sourceId);
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            // Each word must match somewhere, but not all in the same column.
            //
            // "Toyota 86 GT Limited" is one car spread across three fields - Make "Toyota",
            // Model "86", Variant "GT Limited" - so matching the phrase against any single
            // column finds nothing at all. That is how people type a search, and it returned
            // an empty page for a car sitting in the table.
            //
            // So: AND across the terms, OR across the columns. Every word has to appear in one
            // of the three, which keeps "Toyota Hiace" from matching every Toyota while still
            // finding a car whose words are split across columns.
            foreach (var pattern in BuildSearchPatterns(query.Text))
            {
                // Raw make and model, not the normalized ids: an unmapped alias leaves MakeId
                // null and the vehicle must stay findable by the text it arrived with, or
                // normalization gaps become silent inventory loss (canonical model spec
                // section 7).
                listings = listings.Where(l =>
                    (l.Vehicle.Make != null && EF.Functions.Like(l.Vehicle.Make, pattern, LikeEscape))
                    || (l.Vehicle.Model != null && EF.Functions.Like(l.Vehicle.Model, pattern, LikeEscape))
                    || (l.Vehicle.Variant != null && EF.Functions.Like(l.Vehicle.Variant, pattern, LikeEscape)));
            }
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

        var joined =
            from l in listings
            join o in overlays on l.VehicleId equals o.VehicleId into overlayGroup
            from overlay in overlayGroup.DefaultIfEmpty()
            where overlay == null || !overlay.IsHidden
            select new { Listing = l, Overlay = overlay };

        // One row per CAR, not per listing.
        //
        // A vehicle offered by three exporters has three listings, and returning them
        // individually puts the same physical car on screen three times - which makes
        // deduplication look broken precisely when it has just worked. The listings stay
        // separate in the data, as master prompt section 7 requires; what changes is that
        // search answers "which cars match", and the detail view answers "who is offering
        // this one, and at what price".
        //
        // The representative offer is the cheapest with a known base price, falling back to
        // the most recently seen. Cheapest is what a buyer would act on, and a listing whose
        // price could not be converted must not win by being null.
        // Aggregate per vehicle. Only translatable aggregates are used - Count, Distinct
        // Count, Min, Max - because an ordered First() inside a group projection does not
        // translate to SQL, and discovering that at runtime is a 500 rather than a compile
        // error. The grouping key carries the fields sorting needs, so no join back is
        // required to order the page.
        var grouped = joined
            .GroupBy(x => new
            {
                x.Listing.VehicleId,
                x.Listing.Vehicle.ModelYear,
                x.Listing.Vehicle.Mileage,
            })
            .Select(g => new
            {
                g.Key.VehicleId,
                g.Key.ModelYear,
                g.Key.Mileage,
                OfferCount = g.Count(),
                SourceCount = g.Select(x => x.Listing.VehicleSourceId).Distinct().Count(),

                // SQL MIN ignores nulls, which is exactly right: the cheapest KNOWN price,
                // with unconvertible listings neither winning nor excluding the car.
                MinBasePrice = g.Min(x => x.Listing.PriceBaseCurrency),

                // The freshest confirmation across all offers. A car one exporter last saw
                // six weeks ago and another saw yesterday is a car seen yesterday.
                LastSeenAtUtc = g.Max(x => x.Listing.LastSeenAtUtc),
            });

        var total = await grouped.CountAsync(ct).ConfigureAwait(false);

        grouped = query.Sort switch
        {
            VehicleSearchSort.PriceAscending =>
                grouped.OrderBy(x => x.MinBasePrice ?? decimal.MaxValue),
            VehicleSearchSort.PriceDescending =>
                grouped.OrderByDescending(x => x.MinBasePrice ?? decimal.MinValue),
            VehicleSearchSort.YearDescending =>
                grouped.OrderByDescending(x => x.ModelYear ?? 0),
            VehicleSearchSort.MileageAscending =>
                grouped.OrderBy(x => x.Mileage ?? int.MaxValue),
            _ => grouped.OrderByDescending(x => x.LastSeenAtUtc),
        };

        var pageRows = await grouped
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var vehicleIds = pageRows.Select(r => r.VehicleId).ToList();

        // A second query for the page's cars, rather than one clever query for everything.
        // Bounded by the page size, so this is one extra round trip regardless of catalog
        // size, and it keeps the aggregate query simple enough to be certain it translates.
        var cars = await _db.Vehicles
            .AsNoTracking()
            .Where(v => vehicleIds.Contains(v.Id))
            .Select(v => new
            {
                v.Id,
                v.PublicId,
                v.Make,
                v.Model,
                v.Variant,
                v.ModelYear,
                v.Mileage,
                v.MileageUnit,
                v.SteeringSide,
                v.FuelType,
                v.Transmission,
                PrimaryImageUrl = v.Images.OrderBy(i => i.SortOrder).Select(i => i.ImageUrl).FirstOrDefault(),

                // The cheapest offer decides what the card shows, so price, currency, incoterm
                // and attribution all come from the same listing. Mixing a minimum price with
                // another listing's incoterm would put an FOB number under a CIF label.
                Best = v.Listings
                    .Where(l => l.IsActive)
                    .OrderBy(l => l.PriceBaseCurrency == null ? 1 : 0)
                    .ThenBy(l => l.PriceBaseCurrency)
                    .ThenByDescending(l => l.LastSeenAtUtc)
                    .Select(l => new
                    {
                        l.Price,
                        l.CurrencyCode,
                        l.PriceBaseCurrency,
                        l.BaseCurrencyCode,
                        l.PriceType,
                        SourceName = l.VehicleSource.Name,
                        l.SourceUrl,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var overlayById = await _db.TenantVehicles
            .AsNoTracking()
            .Where(o => vehicleIds.Contains(o.VehicleId))
            .ToDictionaryAsync(o => o.VehicleId, ct)
            .ConfigureAwait(false);

        var carById = cars.ToDictionary(c => c.Id);

        // Assembled in the page's order, which the database already decided. Re-sorting here
        // would silently ignore the sort the caller asked for.
        var hits = new List<VehicleSearchHit>(pageRows.Count);

        foreach (var row in pageRows)
        {
            if (!carById.TryGetValue(row.VehicleId, out var car))
            {
                continue;
            }

            overlayById.TryGetValue(row.VehicleId, out var overlay);

            hits.Add(new VehicleSearchHit
            {
                PublicId = car.PublicId,
                Make = car.Make,
                Model = car.Model,
                Variant = car.Variant,
                ModelYear = car.ModelYear,
                Mileage = car.Mileage,
                MileageUnit = car.MileageUnit,
                SteeringSide = car.SteeringSide,
                FuelType = car.FuelType,
                Transmission = car.Transmission,
                Price = car.Best?.Price,
                CurrencyCode = car.Best?.CurrencyCode,
                PriceBaseCurrency = car.Best?.PriceBaseCurrency,
                BaseCurrencyCode = car.Best?.BaseCurrencyCode,
                PriceType = car.Best?.PriceType ?? PriceType.Unknown,

                // Attribution names one source only when there is one. Putting a single
                // exporter's name on a card that aggregates three would misattribute the
                // other two, and attribution is a POC acceptance criterion.
                SourceName = row.SourceCount == 1 ? car.Best?.SourceName : null,
                SourceUrl = row.SourceCount == 1 ? car.Best?.SourceUrl : null,

                PrimaryImageUrl = car.PrimaryImageUrl,
                LastSeenAtUtc = row.LastSeenAtUtc,
                OfferCount = row.OfferCount,
                SourceCount = row.SourceCount,
                TenantPrice = overlay?.TenantPrice,
                TenantCurrencyCode = overlay?.TenantCurrencyCode,
            });
        }

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
