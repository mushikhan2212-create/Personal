namespace CarDealer.Domain.Entities;

/// <summary>
/// Join between a listing and an image, because two sources can offer the same physical car
/// with different photo sets.
/// </summary>
public class VehicleListingImage
{
    public long VehicleListingId { get; set; }

    public VehicleListing VehicleListing { get; set; } = null!;

    public long VehicleImageId { get; set; }

    public VehicleImage VehicleImage { get; set; } = null!;
}
