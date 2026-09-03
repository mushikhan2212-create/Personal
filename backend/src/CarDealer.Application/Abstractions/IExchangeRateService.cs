using CarDealer.Application.VehicleSources;

namespace CarDealer.Application.Abstractions;

/// <summary>
/// Converts listing prices into the catalog's base currency (decision D6).
/// </summary>
/// <remarks>
/// A separate step from normalization, and deliberately so. A normalizer maps what a source
/// said; converting a price is asserting something the source did not say, using a rate that
/// has a date and a provenance. Conflating the two would bury an assumption inside a mapping.
/// </remarks>
public interface IExchangeRateService
{
    /// <summary>The currency search ranges and sorting are expressed in.</summary>
    string BaseCurrencyCode { get; }

    /// <summary>
    /// Converts an amount, or returns null when no rate is known for that currency.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess. A listing whose currency has no rate is excluded from price
    /// range filters, which is decision D6's explicit instruction: a car whose price cannot be
    /// compared is not a free car, and inventing a rate to make it sortable would put a number
    /// in front of a customer that no source ever quoted.
    /// </remarks>
    Task<CurrencyConversion?> ToBaseAsync(
        decimal amount, string? currencyCode, CancellationToken ct = default);
}
