using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// One source's offer of a <see cref="Vehicle"/>. Everything two sources could disagree
/// about lives here rather than on the vehicle.
/// </summary>
public class VehicleListing : AuditableEntity, IOptionallyTenantScoped
{
    /// <summary>Null = global catalog row, matching the vehicle it belongs to (decision D1).</summary>
    public long? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Persisted computed column, ISNULL(TenantId, 0). Without it the unique index over
    /// (TenantId, VehicleSourceId, ExternalListingId) stops preventing duplicates on exactly
    /// the global rows that matter most, because SQL Server treats NULLs as distinct - every
    /// sync run would silently insert another copy.
    /// </summary>
    public long TenantScope { get; private set; }

    public long VehicleId { get; set; }

    public Vehicle Vehicle { get; set; } = null!;

    public long VehicleSourceId { get; set; }

    public VehicleSource VehicleSource { get; set; } = null!;

    /// <summary>The source's own id for this listing. Half of the deduplication key.</summary>
    public string? ExternalListingId { get; set; }

    public string? SourceUrl { get; set; }

    /// <summary>The source's own status string, kept unmapped for attribution.</summary>
    public string? SourceStatus { get; set; }

    // --- Price ----------------------------------------------------------------------------

    public decimal? Price { get; set; }

    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Incoterm the price is quoted under. Comparing an FOB price against a CIF price without
    /// knowing which is which compares two different numbers.
    /// </summary>
    public PriceType PriceType { get; set; }

    /// <summary>
    /// Price converted to a single base currency at sync time, so that cross-currency range
    /// filters can be served by an index (decision D6).
    /// </summary>
    /// <remarks>
    /// Null when FX was unavailable at sync time - storing a guess would be worse. Nulls are
    /// excluded from base-currency range filters.
    /// </remarks>
    public decimal? PriceBaseCurrency { get; set; }

    public string? BaseCurrencyCode { get; set; }

    /// <summary>
    /// Pins the exact rate used. Reports read the pinned rate, so a price-trend report does
    /// not silently rewrite itself as rates move.
    /// </summary>
    public long? ExchangeRateId { get; set; }

    public ExchangeRate? ExchangeRate { get; set; }

    // --- Freight and logistics --------------------------------------------------------------

    /// <summary>
    /// Quoted freight to <see cref="PortOfDischarge"/>. Meaningless without it: freight with
    /// no destination is unknown, never zero.
    /// </summary>
    public decimal? FreightCost { get; set; }

    /// <summary>May differ from <see cref="CurrencyCode"/>.</summary>
    public string? FreightCurrencyCode { get; set; }

    public string? PortOfLoading { get; set; }

    public string? PortOfDischarge { get; set; }

    public string? LocationCountryCode { get; set; }

    public string? LocationCity { get; set; }

    // --- Provenance -------------------------------------------------------------------------

    /// <summary>
    /// The source record exactly as received, kept for debugging and reprocessing
    /// (SQL schema spec section 8).
    /// </summary>
    /// <remarks>
    /// Stored separately from the canonical columns and never used as a substitute for them.
    /// Master prompt section 3 requires raw payloads be preserved so normalization can be
    /// re-run without re-fetching.
    /// </remarks>
    public string? RawPayload { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }

    public DateTime LastSeenAtUtc { get; set; }

    public DateTime? LastSyncedAtUtc { get; set; }

    public bool IsActive { get; set; } = true;
}
