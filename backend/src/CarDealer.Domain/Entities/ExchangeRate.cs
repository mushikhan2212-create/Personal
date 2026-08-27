using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// One FX rate at one moment. Append-only (decision D6).
/// </summary>
/// <remarks>
/// Never update a row here. Listings reference a specific rate by id, and rewriting a rate
/// would retroactively rewrite every historical price that pinned it - a price-trend report
/// that silently changes is worse than no report.
/// </remarks>
public class ExchangeRate : Entity
{
    public string BaseCurrencyCode { get; set; } = string.Empty;

    public string QuoteCurrencyCode { get; set; } = string.Empty;

    /// <summary>decimal(18,8) - wider than money, because FX needs the precision.</summary>
    public decimal Rate { get; set; }

    public DateTime AsOfUtc { get; set; }

    /// <summary>The rate provider, recorded so a bad feed can be traced.</summary>
    public string Source { get; set; } = string.Empty;
}
