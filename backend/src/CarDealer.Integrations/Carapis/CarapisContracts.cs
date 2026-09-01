using System.Text.Json.Serialization;

namespace CarDealer.Integrations.Carapis;

// Wire shapes for api.carapis.com, transcribed from real responses rather than from the
// vendor documentation - the two disagree, and docs/spec/07-carapis-api.md records where.
//
// These types stay internal to this project. Nothing outside Integrations references them,
// which is what keeps acceptance criterion H6 true.

internal sealed record CarapisPage<T>
{
    [JsonPropertyName("count")] public int Count { get; init; }

    [JsonPropertyName("page")] public int Page { get; init; }

    [JsonPropertyName("pages")] public int Pages { get; init; }

    [JsonPropertyName("page_size")] public int PageSize { get; init; }

    [JsonPropertyName("has_next")] public bool HasNext { get; init; }

    [JsonPropertyName("results")] public List<T> Results { get; init; } = [];
}

/// <summary>
/// One vehicle. The list endpoint returns a narrower projection than the detail endpoint, so
/// the fields absent from the list are simply null there - one type serves both, and the
/// normalizer must not assume a field is populated merely because it exists here.
/// </summary>
internal sealed record CarapisVehicle
{
    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("source_code")] public string? SourceCode { get; init; }

    [JsonPropertyName("brand_name")] public string? BrandName { get; init; }

    [JsonPropertyName("model_name")] public string? ModelName { get; init; }

    [JsonPropertyName("trim")] public string? Trim { get; init; }

    [JsonPropertyName("year")] public int? Year { get; init; }

    [JsonPropertyName("mileage")] public int? Mileage { get; init; }

    [JsonPropertyName("engine_cc")] public int? EngineCc { get; init; }

    [JsonPropertyName("seat_count")] public byte? SeatCount { get; init; }

    [JsonPropertyName("fuel_type")] public string? FuelType { get; init; }

    [JsonPropertyName("transmission")] public string? Transmission { get; init; }

    [JsonPropertyName("body_type")] public string? BodyType { get; init; }

    [JsonPropertyName("color")] public string? Color { get; init; }

    [JsonPropertyName("drive_type")] public string? DriveType { get; init; }

    [JsonPropertyName("region")] public string? Region { get; init; }

    // --- Price. price_usd is indicative only; see docs/spec/07-carapis-api.md section 5.2 ---

    [JsonPropertyName("price_usd")] public decimal? PriceUsd { get; init; }

    [JsonPropertyName("price_original")] public string? PriceOriginal { get; init; }

    [JsonPropertyName("price_original_currency")] public string? PriceOriginalCurrency { get; init; }

    // --- Detail-only identifiers. Empty string, not null, when absent ---

    [JsonPropertyName("vin")] public string? Vin { get; init; }

    /// <summary>
    /// A registration plate on Korean records, not a chassis number. Deliberately never mapped
    /// to <c>Vehicles.ChassisNumber</c> - see section 5.2 - and personal data besides (O3).
    /// </summary>
    [JsonPropertyName("vehicle_no")] public string? VehicleNo { get; init; }

    [JsonPropertyName("listing_id")] public string? ListingId { get; init; }

    [JsonPropertyName("listing_url")] public string? ListingUrl { get; init; }

    [JsonPropertyName("description")] public string? Description { get; init; }

    // --- Lifecycle. Tri-state booleans: null is not false ---

    [JsonPropertyName("is_available")] public bool? IsAvailable { get; init; }

    [JsonPropertyName("has_accident")] public bool? HasAccident { get; init; }

    [JsonPropertyName("inspection_passed")] public bool? InspectionPassed { get; init; }

    [JsonPropertyName("first_seen_at")] public DateTime? FirstSeenAt { get; init; }

    [JsonPropertyName("last_seen_at")] public DateTime? LastSeenAt { get; init; }

    [JsonPropertyName("photos")] public List<CarapisPhoto> Photos { get; init; } = [];

    [JsonPropertyName("photos_count")] public int? PhotosCount { get; init; }
}

internal sealed record CarapisPhoto
{
    /// <summary>Relative (<c>/media/...</c>) or absolute, within the same array. Resolve, never assume.</summary>
    [JsonPropertyName("url")] public string? Url { get; init; }

    [JsonPropertyName("original_url")] public string? OriginalUrl { get; init; }

    [JsonPropertyName("is_main")] public bool IsMain { get; init; }

    [JsonPropertyName("photo_type")] public string? PhotoType { get; init; }

    [JsonPropertyName("position")] public int Position { get; init; }
}

internal sealed record CarapisSource
{
    [JsonPropertyName("code")] public string? Code { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("region")] public string? Region { get; init; }

    [JsonPropertyName("country")] public string? Country { get; init; }

    [JsonPropertyName("availability")] public string? Availability { get; init; }

    [JsonPropertyName("last_parsed_at")] public DateTime? LastParsedAt { get; init; }
}
