using CarDealer.Application.Abstractions;
using CarDealer.Application.VehicleSources;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Infrastructure.Pricing;

/// <summary>
/// Converts prices using the newest stored rate for a currency pair (decision D6).
/// </summary>
/// <remarks>
/// Rates are read from the database rather than fetched live, and the one used is pinned onto
/// the listing by id. That is the whole point of D6: a price is reproducible, and re-running a
/// report next year gives the same numbers as today.
///
/// Rates are cached for the lifetime of the scope. A sync converts hundreds of listings across
/// a handful of currencies, and re-querying the same pair for every row would be a query per
/// vehicle to answer a question whose answer cannot change mid-run.
/// </remarks>
public sealed class ExchangeRateService : IExchangeRateService
{
    private readonly CarDealerDbContext _db;
    private readonly Dictionary<string, CurrencyConversion?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ExchangeRateService(CarDealerDbContext db) => _db = db;

    /// <summary>
    /// USD, the export trade's quoting currency.
    /// </summary>
    /// <remarks>
    /// Fixed for the POC rather than configurable: a per-tenant base currency changes what a
    /// stored PriceBaseCurrency means, so every row would have to record which base it used.
    /// That is a schema decision, not a setting, and not one this phase needs.
    /// </remarks>
    public string BaseCurrencyCode => "USD";

    public async Task<CurrencyConversion?> ToBaseAsync(
        decimal amount, string? currencyCode, CancellationToken ct = default)
    {
        var code = currencyCode?.Trim();

        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        if (string.Equals(code, BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            // Already in the base currency. There is no rate row to pin, and inventing an
            // identity rate would put a fiction in the audit trail.
            return null;
        }

        if (!_cache.TryGetValue(code, out var cached))
        {
            var rate = await _db.ExchangeRates
                .AsNoTracking()
                .Where(r => r.QuoteCurrencyCode == code && r.BaseCurrencyCode == BaseCurrencyCode)
                .OrderByDescending(r => r.AsOfUtc)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            cached = rate is null || rate.Rate <= 0
                ? null
                : new CurrencyConversion(0m, rate.Id, rate.Rate);

            _cache[code] = cached;
        }

        if (cached is not { } found)
        {
            return null;
        }

        // Rate is quote-per-base - JPY per USD - so converting into the base divides.
        var converted = Math.Round(amount / found.Rate, 2, MidpointRounding.AwayFromZero);

        return found with { Amount = converted };
    }
}
