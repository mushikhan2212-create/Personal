using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A physical car, independent of who is selling it.
/// </summary>
/// <remarks>
/// The organising rule (docs/spec/03-canonical-vehicle-model.md): a Vehicle is a physical
/// car, a <see cref="VehicleListing"/> is one source's offer of that car. Anything two
/// sources could disagree about - price, incoterm, freight, availability - is listing data
/// and does not belong here.
///
/// TenantId is nullable (decision D1). Null means the global catalog, populated from shared
/// sources; non-null means one tenant's private inventory. Deduplication and Japanese to
/// English normalization then run once over the global rows rather than once per tenant.
///
/// The cost of that is real: tenant isolation for this table is a read filter
/// (TenantId == null || TenantId == current) rather than flat equality, which also permits
/// UPDATE and DELETE against global rows unless writes are guarded separately. See
/// docs/spec/04-schema-delta.md section 1.4, case 3.
///
/// Every column added in Phase 0.5 is nullable: sources populate them inconsistently, and
/// master prompt section 8 requires the POC to measure completeness. That measurement is
/// what tells us which fields are real.
/// </remarks>
public class Vehicle : AuditableEntity, IOptionallyTenantScoped
{
    /// <summary>Null = global catalog row. Non-null = one tenant's private inventory.</summary>
    public long? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Persisted computed column, ISNULL(TenantId, 0). Exists because SQL Server treats NULLs
    /// as distinct in unique indexes, which would let every sync run insert another copy of
    /// the same global listing (docs/spec/04-schema-delta.md section 1.2). Never assigned in
    /// code - the database computes it.
    /// </summary>
    public long TenantScope { get; private set; }

    public Guid PublicId { get; set; }

    // --- Raw source values, kept verbatim for attribution and debugging -------------------

    /// <summary>Raw make string as received. <see cref="MakeId"/> carries the normalized value.</summary>
    public string? Make { get; set; }

    /// <summary>Raw model string as received. <see cref="ModelId"/> carries the normalized value.</summary>
    public string? Model { get; set; }

    // --- Normalized references (null while unmapped; the vehicle still stays searchable) ---

    public int? MakeId { get; set; }

    public Make? CanonicalMake { get; set; }

    public int? ModelId { get; set; }

    public Model? CanonicalModel { get; set; }

    // --- Specification --------------------------------------------------------------------

    public string? Variant { get; set; }

    public int? ModelYear { get; set; }

    /// <summary>
    /// First registration. Distinct from <see cref="ModelYear"/>, and the distinction has legal
    /// force: destination import-age rules key on registration, not model year. Carried so a
    /// rules table can be added later; no eligibility filter is applied until it exists.
    /// </summary>
    public DateOnly? RegistrationDate { get; set; }

    public string? BodyType { get; set; }

    public string? Engine { get; set; }

    public int? EngineDisplacementCc { get; set; }

    public FuelType FuelType { get; set; }

    public Transmission Transmission { get; set; }

    public Drivetrain Drivetrain { get; set; }

    /// <summary>The single most-used filter in this trade (decision D5).</summary>
    public SteeringSide SteeringSide { get; set; }

    public byte? Doors { get; set; }

    public byte? Seats { get; set; }

    public int? Mileage { get; set; }

    public MileageUnit MileageUnit { get; set; }

    public string? ExteriorColor { get; set; }

    public string? InteriorColor { get; set; }

    // --- Condition and auction ------------------------------------------------------------

    public string? Condition { get; set; }

    /// <summary>
    /// Overall grade from the auction sheet, stored verbatim as a short string rather than an
    /// enum because the vocabulary varies between auction houses
    /// (docs/spec/03-canonical-vehicle-model.md section 4).
    /// </summary>
    public string? AuctionGrade { get; set; }

    /// <summary>Interior letter grade, graded separately from the body.</summary>
    public string? InteriorGrade { get; set; }

    /// <summary>
    /// Numeric reading of <see cref="AuctionGrade"/> where one can be parsed, so that
    /// "grade 4 and above" is a range filter.
    /// </summary>
    /// <remarks>
    /// R and RA mark a repaired vehicle and have no numeric equivalent. They must leave this
    /// null, and numeric grade filters must exclude nulls - otherwise a repaired car passes a
    /// "grade 4 and above" filter.
    /// </remarks>
    public decimal? InspectionScore { get; set; }

    // --- Identifiers ----------------------------------------------------------------------

    /// <summary>Recorded only where legally and contractually available (master prompt section 7).</summary>
    public string? Vin { get; set; }

    public string? ChassisNumber { get; set; }

    /// <summary>Auction lot or dealer stock number. Also the third-choice dedup identifier.</summary>
    public string? LotNumber { get; set; }

    public VehicleStatus Status { get; set; } = VehicleStatus.Active;

    // --- Deduplication --------------------------------------------------------------------

    /// <summary>
    /// First available strong identifier, in strict precedence: normalized VIN, else
    /// normalized chassis number, else source id plus normalized lot number
    /// (docs/spec/04-schema-delta.md section 3.1).
    /// </summary>
    /// <remarks>
    /// Null when no strong identifier exists, and a null hash never matches anything -
    /// including another null. Those vehicles enter as distinct rows and are consolidated
    /// only through the review queue. Auto-merge happens on exact equality of a non-null hash
    /// and on nothing else (decision D3): under D1 the catalog is global, so a wrong merge
    /// shows a wrong price to every tenant at once.
    /// </remarks>
    public string? CanonicalHash { get; set; }

    /// <summary>Which rule produced the hash, so a VIN match can outrank a lot-number match.</summary>
    public CanonicalHashSource? CanonicalHashSource { get; set; }

    public ICollection<VehicleListing> Listings { get; set; } = new List<VehicleListing>();

    public ICollection<VehicleImage> Images { get; set; } = new List<VehicleImage>();
}
