using CarDealer.Domain.Enums;

namespace CarDealer.Application.VehicleSources;

/// <summary>
/// The capability set a vehicle source can offer (master prompt section 6).
/// </summary>
/// <remarks>
/// Split rather than one fat interface because sources genuinely differ: a dealer CSV feed
/// can be synced but not searched remotely, and an availability probe is meaningless for a
/// file. A provider implements only what it can honestly do, and callers ask the container
/// for the capability they need rather than for a named vendor.
///
/// Nothing here mentions HTTP, JSON or Carapis. That is the point of master prompt section 5:
/// replacing the provider is a registration change, not a rewrite. Acceptance criterion H6
/// holds because this project references no vendor SDK.
/// </remarks>
public interface IVehicleSourceProvider
{
    /// <summary>Stable code matching <c>VehicleSources.Code</c>.</summary>
    string SourceCode { get; }

    VehicleSourceProviderType ProviderType { get; }
}

/// <summary>Pulls a filtered page of listings. The POC's main path.</summary>
public interface IVehicleSourceSyncProvider : IVehicleSourceProvider
{
    /// <summary>
    /// Fetches one page. The caller drives paging so quotas stay in the caller's control
    /// (master prompt section 18 forbids unlimited synchronization).
    /// </summary>
    Task<VehicleSourcePage> FetchPageAsync(VehicleSourceQuery query, CancellationToken ct = default);
}

/// <summary>
/// Fetches one record in full. Separate from sync because for some sources it costs an extra
/// request per vehicle, and the caller must be able to decide whether that is worth paying.
/// </summary>
public interface IVehicleSourceDetailProvider : IVehicleSourceProvider
{
    Task<RawVehicleRecord?> FetchDetailAsync(string externalId, CancellationToken ct = default);
}

/// <summary>Lists the sub-sources a provider can serve, with whatever freshness it reports.</summary>
public interface IVehicleSourceCatalogProvider : IVehicleSourceProvider
{
    Task<IReadOnlyList<VehicleSourceDescriptor>> ListSourcesAsync(CancellationToken ct = default);
}

/// <summary>Confirms whether a listing is still offered, without refetching everything.</summary>
public interface IVehicleSourceAvailabilityProvider : IVehicleSourceProvider
{
    Task<bool?> IsStillAvailableAsync(string externalId, CancellationToken ct = default);
}
