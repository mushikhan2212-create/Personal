using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarDealer.Application.VehicleSources;

/// <summary>
/// What a source is allowed to bring into the catalog.
/// </summary>
/// <remarks>
/// Master prompt section 18 forbids unlimited synchronization without filters. Decision D12
/// applies that to Carapis with a permitted-source list; this applies it to the contents of a
/// source, so a file offering a hundred thousand cars cannot quietly become a hundred thousand
/// rows.
///
/// Every list is an allow-list, and an empty or absent one means "no restriction on this
/// dimension" rather than "allow nothing". That direction is deliberate: a filter that is
/// misconfigured should over-ingest visibly, not silently ingest nothing - which is exactly the
/// failure mode that made a 400-vehicle catalog return no search results earlier in this
/// project.
/// </remarks>
public sealed record IngestionFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Permitted makes, case-insensitive. Empty means every make.</summary>
    [JsonPropertyName("makes")]
    public IReadOnlyList<string> Makes { get; init; } = [];

    /// <summary>
    /// Permitted models, case-insensitive, matched as a substring.
    /// </summary>
    /// <remarks>
    /// Substring rather than equality because model naming is not consistent across sources:
    /// one publishes "Hiace", another "HIACE VAN", another "Hiace Van 3.0 DX". Requiring an
    /// exact match would exclude most of the stock the list is meant to include.
    /// </remarks>
    [JsonPropertyName("models")]
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>
    /// Permitted destination markets as ISO country codes, matched against the listing's
    /// stated destinations. Empty means any destination, including unstated.
    /// </summary>
    [JsonPropertyName("destinationMarkets")]
    public IReadOnlyList<string> DestinationMarkets { get; init; } = [];

    /// <summary>Oldest model year accepted. Null means no lower bound.</summary>
    [JsonPropertyName("minYear")]
    public int? MinYear { get; init; }

    /// <summary>Hard ceiling on how many records one run may ingest. Null means no ceiling.</summary>
    [JsonPropertyName("maxRecords")]
    public int? MaxRecords { get; init; }

    /// <summary>Parses a stored filter. Malformed JSON is a configuration error, not a filter.</summary>
    public static IngestionFilter? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IngestionFilter>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "This source's IngestionFilterJson is not valid JSON, so what it is meant to "
                + "permit cannot be determined. Refusing to ingest rather than guessing: "
                + "treating an unreadable filter as 'allow everything' would silently defeat "
                + $"the quota it exists to enforce. {ex.Message}", ex);
        }
    }

    /// <summary>Whether a record's fields fall inside this filter.</summary>
    /// <param name="destinations">
    /// Destinations the listing states. An empty set passes any destination requirement: the
    /// source not saying where a car can go is not the same as saying it cannot go there.
    /// </param>
    public bool Permits(
        string? make, string? model, int? year, IReadOnlyCollection<string>? destinations = null)
    {
        if (Makes.Count > 0
            && !Makes.Any(m => string.Equals(m, make, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Models.Count > 0
            && !Models.Any(m => model is not null
                && model.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (MinYear is { } min && year is { } actual && actual < min)
        {
            return false;
        }

        if (DestinationMarkets.Count > 0 && destinations is { Count: > 0 }
            && !destinations.Any(d => DestinationMarkets.Any(
                allowed => string.Equals(allowed, d, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return true;
    }
}
