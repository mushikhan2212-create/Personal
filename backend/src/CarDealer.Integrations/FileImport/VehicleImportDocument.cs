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
    public string? SourceCode { get; init; }

    /// <summary>When this document was produced. Informational; per-vehicle times govern.</summary>
    public DateTime? CapturedAtUtc { get; init; }

    public List<VehicleImportRecord> Vehicles { get; init; } = [];
}

/// <summary>
/// One vehicle as supplied by an import.
/// </summary>
/// <remarks>
/// Populated by <see cref="VehicleImportRecordConverter"/>, which accepts both camelCase and
/// snake_case for every field plus a few source-specific aliases. The property names here are
/// the canonical spelling; the converter holds the list each one answers to.
/// </remarks>
[JsonConverter(typeof(VehicleImportRecordConverter))]
public sealed class VehicleImportRecord
{
    /// <summary>The source's own headline, e.g. "2022 TOYOTA PASSO 1.0XLPKG".</summary>
    /// <remarks>
    /// Kept because it is often the only place a grade appears - BE FORWARD publishes no
    /// separate trim field, but writes it into the title. The normalizer derives a variant
    /// from it when none is stated, and records that the value was inferred rather than given.
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>
    /// The manufacturer's model code, e.g. M700A or 5BA-M700A.
    /// </summary>
    /// <remarks>
    /// A specification, never an identifier. Every Toyota Passo of a generation carries M700A,
    /// so two different cars share it - mapping this to ChassisNumber would merge every car of
    /// a model into one vehicle. Stored so it can be displayed and searched, and deliberately
    /// kept out of CanonicalIdentity.
    /// </remarks>
    public string? ChassisCode { get; init; }

    public int? Seats { get; init; }

    public int? Doors { get; init; }

    public string? ConditionNotes { get; init; }

    /// <summary>The producer's own stable id for this listing. Required.</summary>
    public string? ExternalId { get; init; }

    public string? ListingUrl { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public int? Year { get; init; }

    public int? Mileage { get; init; }

    /// <summary>"km" or "mi". Absent leaves the unit Unknown rather than assuming kilometres.</summary>
    public string? MileageUnit { get; init; }

    /// <summary>"rhd" / "lhd". The export trade's most-used filter (decision D5).</summary>
    public string? Steering { get; init; }

    public string? FuelType { get; init; }

    public string? Transmission { get; init; }

    public string? Drivetrain { get; init; }

    public string? BodyType { get; init; }

    public int? EngineCc { get; init; }

    public string? ExteriorColor { get; init; }

    public decimal? Price { get; init; }

    public string? Currency { get; init; }

    /// <summary>
    /// The incoterm: "FOB", "CIF", "CFR", "EXW". Optional, and absent means Unknown.
    /// </summary>
    /// <remarks>
    /// Carried explicitly because Carapis publishes no incoterm at all, which is why every
    /// vehicle synced from it prices as Unknown. FOB and CIF differ by the entire cost of
    /// shipping, so an unstated incoterm is left unstated rather than assumed.
    /// </remarks>
    public string? PriceType { get; init; }

    public string? Vin { get; init; }

    public string? ChassisNumber { get; init; }

    public string? LotNumber { get; init; }

    public string? LocationCountry { get; init; }

    public string? LocationCity { get; init; }

    public string? PortOfLoading { get; init; }

    /// <summary>ISO country codes this listing can ship to, for the coverage filter.</summary>
    public List<string> DestinationMarkets { get; init; } = [];

    public List<string> ImageUrls { get; init; } = [];

    /// <summary>
    /// Whether the source still offers this car. Tri-state: absent means it did not say.
    /// </summary>
    public bool? IsAvailable { get; init; }

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
    public DateTime? LastSeenAtUtc { get; init; }

    public DateTime? FirstSeenAtUtc { get; init; }
}
