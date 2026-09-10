namespace CarDealer.Domain.Enums;

// Value 0 is reserved for Unknown throughout, per docs/spec/03-canonical-vehicle-model.md
// section 6. A missing value must stay distinguishable from a real one.

public enum TenantStatus : byte
{
    Unknown = 0,
    Active = 1,
    Suspended = 2,
    Closed = 3,
}

/// <summary>
/// Global account state. NOT for per-tenant suspension.
/// </summary>
/// <remarks>
/// Decision D2: a user identity spans tenants, so suspending this would lock the user
/// out of every tenant they belong to. Per-tenant suspension is
/// <see cref="MembershipStatus.Suspended"/> on TenantUsers. Acceptance criterion C8.
/// </remarks>
public enum UserStatus : byte
{
    Unknown = 0,
    Active = 1,
    Suspended = 2,
    Deactivated = 3,
}

/// <summary>
/// A user's standing within one specific tenant (decision D2).
/// </summary>
public enum MembershipStatus : byte
{
    Unknown = 0,

    /// <summary>Invited but has not accepted. Cannot authenticate into this tenant.</summary>
    Invited = 1,

    /// <summary>Full member. The only status that permits access.</summary>
    Active = 2,

    /// <summary>Blocked from this tenant only; other memberships are unaffected.</summary>
    Suspended = 3,
}

// ---------------------------------------------------------------------------
// Phase 0.5 - canonical vehicle model
// Enumerations defined in docs/spec/03-canonical-vehicle-model.md section 6.
// ---------------------------------------------------------------------------

/// <summary>
/// Right- or left-hand drive. The most-used filter in the export trade and not derivable
/// from any other column (decision D5).
/// </summary>
public enum SteeringSide : byte
{
    Unknown = 0,
    RightHandDrive = 1,
    LeftHandDrive = 2,
}

/// <summary>
/// Canonical vehicle status (master prompt section 7).
/// </summary>
/// <remarks>
/// Unavailable and Expired are distinct on purpose: Unavailable is a statement by the
/// source, Expired is an inference drawn from the listing's absence past its grace period.
/// Only Expired is set by the sync job's grace-period rule.
/// </remarks>
public enum VehicleStatus : byte
{
    Unknown = 0,
    Active = 1,
    Reserved = 2,
    Sold = 3,
    Unavailable = 4,
    Expired = 5,
    Archived = 6,
}

/// <summary>Never compare or range-filter mileage without normalizing the unit first.</summary>
public enum MileageUnit : byte
{
    Unknown = 0,
    Kilometers = 1,
    Miles = 2,
}

public enum Transmission : byte
{
    Unknown = 0,
    Manual = 1,
    Automatic = 2,
    ContinuouslyVariable = 3,
    SemiAutomatic = 4,
    DualClutch = 5,
}

/// <summary>
/// Hybrid and plug-in hybrid are separate because destination emissions and tax rules
/// frequently treat them differently.
/// </summary>
public enum FuelType : byte
{
    Unknown = 0,
    Petrol = 1,
    Diesel = 2,
    Hybrid = 3,
    PluginHybrid = 4,
    Electric = 5,
    Lpg = 6,
    Cng = 7,
    Hydrogen = 8,
}

public enum Drivetrain : byte
{
    Unknown = 0,
    FrontWheelDrive = 1,
    RearWheelDrive = 2,
    AllWheelDrive = 3,
    FourWheelDrive = 4,
}

/// <summary>
/// Incoterm the listing price is quoted under
/// (docs/spec/03-canonical-vehicle-model.md section 5).
/// </summary>
/// <remarks>
/// Search and ranking must never compare prices across different values without normalizing
/// first: FOB and CIF differ by the entire cost of shipping and insurance.
///
/// The specification tables values 1-4 only. Unknown = 0 follows section 6's rule that zero
/// is reserved for Unknown throughout, because sources routinely omit the incoterm and a
/// missing one must not be silently read as EXW.
/// </remarks>
public enum PriceType : byte
{
    Unknown = 0,
    ExWorks = 1,
    FreeOnBoard = 2,
    CostAndFreight = 3,
    CostInsuranceFreight = 4,
}

/// <summary>
/// Which rule produced <c>Vehicles.CanonicalHash</c>
/// (docs/spec/04-schema-delta.md section 3.1).
/// </summary>
/// <remarks>
/// Recorded so a VIN-based match can be trusted more than a lot-number match during review.
/// </remarks>
public enum CanonicalHashSource : byte
{
    Unknown = 0,
    Vin = 1,
    ChassisNumber = 2,
    SourceLotNumber = 3,
}

/// <summary>Review state of a suggested duplicate (docs/spec/04-schema-delta.md section 3.2).</summary>
public enum MatchCandidateStatus : byte
{
    Unknown = 0,
    Pending = 1,
    Merged = 2,
    Rejected = 3,
}

// ---------------------------------------------------------------------------
// Phase 0.5 - vehicle source framework
//
// SQL schema spec section 3 names VehicleSources.ProviderType / .SourceType and the
// SyncJobs status and type columns, but never enumerates any of them. The values below are
// derived from the adapter list in master prompt section 6 and from the counters SyncJobs
// is required to report (master prompt section 8: "counts, duration, errors and provider
// status"). They are an inference, not a quotation - revisit if the source documents differ.
// ---------------------------------------------------------------------------

/// <summary>Which adapter services a source (master prompt section 6).</summary>
public enum VehicleSourceProviderType : byte
{
    Unknown = 0,

    /// <summary>POC only, until commercial and legal approval (master prompt section 6, O2).</summary>
    Carapis = 1,

    DealerCsv = 2,
    DealerExcel = 3,
    DealerXml = 4,
    DealerJson = 5,
    Manual = 6,
}

/// <summary>How records arrive from a source.</summary>
public enum VehicleSourceType : byte
{
    Unknown = 0,
    Api = 1,
    File = 2,
    Manual = 3,
}

public enum SyncJobType : byte
{
    Unknown = 0,
    FullSync = 1,
    IncrementalSync = 2,
    SampleFetch = 3,
}

public enum SyncJobStatus : byte
{
    Unknown = 0,
    Pending = 1,
    Running = 2,
    Succeeded = 3,

    /// <summary>Completed, but at least one record failed. Distinct from Failed, which is the whole run.</summary>
    PartiallySucceeded = 4,

    Failed = 5,
}

public enum SyncJobItemStatus : byte
{
    Unknown = 0,
    Created = 1,
    Updated = 2,
    Unchanged = 3,
    Skipped = 4,
    Failed = 5,
}

/// <summary>Where a customer is in the sales relationship (master prompt section 9).</summary>
public enum CustomerStatus : byte
{
    Unknown = 0,

    /// <summary>Enquired, not yet qualified.</summary>
    Lead = 1,

    /// <summary>A real buyer with a real requirement.</summary>
    Active = 2,

    /// <summary>Has bought at least once.</summary>
    Customer = 3,

    /// <summary>Went quiet. Kept, because they come back.</summary>
    Dormant = 4,

    /// <summary>Explicitly not to be contacted again.</summary>
    Closed = 5,
}

/// <summary>How a customer first reached the dealer.</summary>
/// <remarks>
/// Deliberately coarse. A finer taxonomy is worth building once there is data to say which
/// distinctions pay for themselves; inventing twenty values now would mostly produce twenty
/// ways to spell "unknown".
/// </remarks>
public enum LeadSource : byte
{
    Unknown = 0,
    WalkIn = 1,
    Referral = 2,
    Website = 3,
    WhatsApp = 4,
    SocialMedia = 5,
    Marketplace = 6,
    Repeat = 7,
}

/// <summary>Whether a requirement is still being shopped for.</summary>
public enum RequirementStatus : byte
{
    Unknown = 0,

    /// <summary>Still looking; this is what matching runs against.</summary>
    Open = 1,

    /// <summary>Paused by the customer, kept so it can be resumed.</summary>
    OnHold = 2,

    /// <summary>A car was bought against it.</summary>
    Fulfilled = 3,

    /// <summary>Abandoned. Kept for the demand reporting Phase 3 will want.</summary>
    Cancelled = 4,
}

/// <summary>Where a recommendation's ordering came from.</summary>
/// <remarks>
/// Stored because a fallback ordering and a paid-for ranking are otherwise indistinguishable
/// once they are rows in the same table - and the ratio between them is what tells you whether
/// a provider is earning its keep.
/// </remarks>
public enum RecommendationSource : byte
{
    Unknown = 0,

    /// <summary>The deterministic filter order, used when no model answer was usable.</summary>
    Deterministic = 1,

    /// <summary>A model's ranking, which passed every guard.</summary>
    Ai = 2,
}

/// <summary>How a call to an AI provider ended.</summary>
public enum AIRequestStatus : byte
{
    Unknown = 0,

    /// <summary>Sent, no answer yet.</summary>
    Pending = 1,

    /// <summary>Answered, and the answer passed the guards.</summary>
    Succeeded = 2,

    /// <summary>No answer: a network failure, a timeout, or a refusal.</summary>
    Failed = 3,

    /// <summary>
    /// Answered, but the answer failed a guard and was thrown away.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Failed"/> on purpose. A provider that answers promptly and
    /// wrongly is a different problem from one that does not answer, it is billed for either
    /// way, and only this status tells the two apart.
    /// </remarks>
    Rejected = 4,
}
