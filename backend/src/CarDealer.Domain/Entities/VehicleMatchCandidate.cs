using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A suggested duplicate awaiting human review. Fuzzy similarity writes here and merges
/// nothing (decision D3).
/// </summary>
/// <remarks>
/// The asymmetry is what drives this: a missed merge shows a duplicate, a wrong merge can
/// lose a sale or sell a car twice - and under D1 the catalog is global, so a wrong merge is
/// wrong for every tenant simultaneously. No similarity threshold auto-merges, and none will
/// until there is real multi-source data to tune against.
/// </remarks>
public class VehicleMatchCandidate : Entity, IPubliclyAddressable
{
    /// <summary>
    /// Stable external identifier, so the route never exposes the sequential key.
    /// </summary>
    /// <remarks>
    /// Present because this entity is addressed at the <b>top level</b> of a route, where the
    /// id is the only thing identifying the record - see decision D17. Entities reached through
    /// a nested route keep their integer keys, because the parent's own PublicId already gates
    /// access to them.
    /// </remarks>
    public Guid PublicId { get; set; }

    /// <summary>
    /// Always the lower of the two vehicle ids. Pairs are normalized before insert, otherwise
    /// every pair is stored twice and the unique constraint does not catch it
    /// (docs/spec/04-schema-delta.md section 3.2).
    /// </summary>
    public long VehicleId { get; set; }

    public Vehicle Vehicle { get; set; } = null!;

    /// <summary>Always the higher of the two vehicle ids.</summary>
    public long CandidateVehicleId { get; set; }

    public Vehicle CandidateVehicle { get; set; } = null!;

    /// <summary>0.0000 to 1.0000.</summary>
    public decimal Score { get; set; }

    /// <summary>
    /// Which signals fired and their weights. Exists so a reviewer can see why two vehicles
    /// were suggested - a bare score is not reviewable.
    /// </summary>
    public string? SignalsJson { get; set; }

    public MatchCandidateStatus Status { get; set; } = MatchCandidateStatus.Pending;

    public long? ReviewedByUserId { get; set; }

    public User? ReviewedByUser { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }
}
