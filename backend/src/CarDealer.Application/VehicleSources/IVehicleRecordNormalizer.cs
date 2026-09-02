using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;

namespace CarDealer.Application.VehicleSources;

/// <summary>Result of normalizing one record, with what could not be mapped made explicit.</summary>
/// <remarks>
/// Lives in Application rather than beside any one provider: it is the shape every source
/// converges on, and the sync pipeline is written against it rather than against a vendor.
/// </remarks>
public sealed record NormalizedVehicle
{
    public required Vehicle Vehicle { get; init; }

    public required VehicleListing Listing { get; init; }

    public required IReadOnlyList<VehicleImage> Images { get; init; }

    /// <summary>
    /// Facts the source did not state and that were inferred from free text. Recorded so a
    /// derived value is never mistaken for a declared one.
    /// </summary>
    public IReadOnlyList<string> InferredFields { get; init; } = [];
}

/// <summary>
/// Turns one source's raw payload into canonical entities.
/// </summary>
/// <remarks>
/// Separate from <see cref="IVehicleSourceProvider"/> because fetching and interpreting are
/// different jobs with different failure modes: a provider breaks when the network or the
/// credentials break, a normalizer breaks when the payload's shape changes. They are also
/// replaced at different times - the same JSON import format can arrive from a file today and
/// an authorized feed later, and only the fetching half changes.
///
/// Every implementation works from the stored payload rather than a live response, so
/// normalization can be re-run over what was already fetched when a mapping improves - and
/// these mappings do improve, because much of any first mapping is provisional
/// (SQL schema spec section 8).
/// </remarks>
public interface IVehicleRecordNormalizer
{
    /// <summary>
    /// Which provider type's payloads this understands. The sync pipeline matches this against
    /// the <see cref="Domain.Entities.VehicleSource"/> being synced.
    /// </summary>
    VehicleSourceProviderType ProviderType { get; }

    /// <summary>
    /// Maps one record, or returns null when the payload is not one this can read at all -
    /// which the caller records as a failed item rather than treating as an empty catalog.
    /// </summary>
    NormalizedVehicle? Normalize(RawVehicleRecord record, long vehicleSourceId, string? mediaBaseUrl = null);
}
