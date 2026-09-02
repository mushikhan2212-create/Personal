using System.Text.Json.Serialization;

namespace CarDealer.Integrations.FileImport;

/// <summary>
/// The import file format, documented in docs/spec/08-import-format.md.
/// </summary>
/// <remarks>
/// This is a published contract: whatever produces the file - an authorized partner feed, a
/// dealer's export, a tool run outside this platform - targets these names. So it is written
/// against the canonical model of decision D5 rather than against any one source's shape, and
/// it is deliberately not a mirror of the Carapis payload.
/// </remarks>
public sealed class VehicleImportDocument
{
    /// <summary>Which registered VehicleSource these records belong to.</summary>
    [JsonPropertyName("sourceCode")] public string? SourceCode { get; init; }

    /// <summary>When this document was produced. Informational; per-vehicle times govern.</summary>
    [JsonPropertyName("capturedAtUtc")] public DateTime? CapturedAtUtc { get; init; }

    [JsonPropertyName("vehicles")] public List<VehicleImportRecord> Vehicles { get; init; } = [];
}

/// <summary>One vehicle as supplied by an import.</summary>
public sealed class VehicleImportRecord
{
    /// <summary>The producer's own stable id for this listing. Required.</summary>
    [JsonPropertyName("externalId")] public string? ExternalId { get; init; }

    [JsonPropertyName("listingUrl")] public string? ListingUrl { get; init; }

    [JsonPropertyName("make")] public string? Make { get; init; }

    [JsonPropertyName("model")] public string? Model { get; init; }

    [JsonPropertyName("variant")] public string? Variant { get; init; }

    [JsonPropertyName("year")] public int? Year { get; init; }

    [JsonPropertyName("mileage")] public int? Mileage { get; init; }

    /// <summary>"km" or "mi". Absent leaves the unit Unknown rather than assuming kilometres.</summary>
    [JsonPropertyName("mileageUnit")] public string? MileageUnit { get; init; }

    /// <summary>"rhd" / "lhd". The export trade's most-used filter (decision D5).</summary>
    [JsonPropertyName("steering")] public string? Steering { get; init; }

    [JsonPropertyName("fuelType")] public string? FuelType { get; init; }

    [JsonPropertyName("transmission")] public string? Transmission { get; init; }

    [JsonPropertyName("drivetrain")] public string? Drivetrain { get; init; }

    [JsonPropertyName("bodyType")] public string? BodyType { get; init; }

    [JsonPropertyName("engineCc")] public int? EngineCc { get; init; }

    [JsonPropertyName("exteriorColor")] public string? ExteriorColor { get; init; }

    [JsonPropertyName("price")] public decimal? Price { get; init; }

    [JsonPropertyName("currency")] public string? Currency { get; init; }

    /// <summary>
    /// The incoterm: "FOB", "CIF", "CFR", "EXW". Optional, and absent means Unknown.
    /// </summary>
    /// <remarks>
    /// Carried explicitly because Carapis publishes no incoterm at all, which is why every
    /// vehicle synced from it prices as Unknown. FOB and CIF differ by the entire cost of
    /// shipping, so an unstated incoterm is left unstated rather than assumed.
    /// </remarks>
    [JsonPropertyName("priceType")] public string? PriceType { get; init; }

    [JsonPropertyName("vin")] public string? Vin { get; init; }

    [JsonPropertyName("chassisNumber")] public string? ChassisNumber { get; init; }

    [JsonPropertyName("lotNumber")] public string? LotNumber { get; init; }

    [JsonPropertyName("locationCountry")] public string? LocationCountry { get; init; }

    [JsonPropertyName("locationCity")] public string? LocationCity { get; init; }

    [JsonPropertyName("portOfLoading")] public string? PortOfLoading { get; init; }

    /// <summary>ISO country codes this listing can ship to, for the coverage filter.</summary>
    [JsonPropertyName("destinationMarkets")] public List<string> DestinationMarkets { get; init; } = [];

    [JsonPropertyName("imageUrls")] public List<string> ImageUrls { get; init; } = [];

    /// <summary>
    /// Whether the source still offers this car. Tri-state: absent means it did not say.
    /// </summary>
    [JsonPropertyName("isAvailable")] public bool? IsAvailable { get; init; }

    /// <summary>
    /// When the producer last confirmed this listing exists. Required.
    /// </summary>
    /// <remarks>
    /// The one field this format will not accept as missing, because its absence is the exact
    /// defect that made the previous source unusable: every Carapis record has first_seen_at
    /// equal to last_seen_at, meaning it was seen once and never revisited, so availability was
    /// frozen at capture and a car sold weeks ago still read as for sale. An import that cannot
    /// say when it last saw a car is importing that same problem.
    /// </remarks>
    [JsonPropertyName("lastSeenAtUtc")] public DateTime? LastSeenAtUtc { get; init; }

    [JsonPropertyName("firstSeenAtUtc")] public DateTime? FirstSeenAtUtc { get; init; }
}
