using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// Record of one merge, and everything needed to undo it.
/// </summary>
/// <remarks>
/// Merges must be reversible because under decision D1 a bad merge is visible to every
/// tenant at once. On merge the listings, images and recommendations are repointed to the
/// surviving vehicle and the merged vehicle is set to Archived - never deleted. Recording
/// which listings moved is what makes reversal possible.
/// </remarks>
public class VehicleMergeHistory : Entity
{
    public long SurvivingVehicleId { get; set; }

    public Vehicle SurvivingVehicle { get; set; } = null!;

    public long MergedVehicleId { get; set; }

    public Vehicle MergedVehicle { get; set; } = null!;

    /// <summary>Null means an automatic merge on an exact strong identifier.</summary>
    public long? MergedByUserId { get; set; }

    public User? MergedByUser { get; set; }

    /// <summary>Which identifier matched, or the reviewer's note.</summary>
    public string? ReasonsJson { get; set; }

    /// <summary>The listing ids that were repointed, so the merge can be undone.</summary>
    public string? RepointedListingIdsJson { get; set; }

    public DateTime MergedAtUtc { get; set; }

    public DateTime? RevertedAtUtc { get; set; }

    public long? RevertedByUserId { get; set; }

    public User? RevertedByUser { get; set; }
}
