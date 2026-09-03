namespace CarDealer.Application.VehicleSources;

/// <summary>
/// A price converted to the base currency, together with the rate that produced it.
/// </summary>
/// <remarks>
/// The rate id travels with the amount because decision D6 requires a price to be explainable.
/// Storing only the converted number leaves nobody able to answer "why is this car $14,333?"
/// six months later, and a price nobody can explain is a price nobody can defend to a customer.
/// </remarks>
public readonly record struct CurrencyConversion(decimal Amount, long ExchangeRateId, decimal Rate);
