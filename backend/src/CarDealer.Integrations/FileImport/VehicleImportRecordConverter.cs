using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarDealer.Integrations.FileImport;

/// <summary>
/// Reads an import record, accepting either spelling of every field.
/// </summary>
/// <remarks>
/// The format was published in camelCase; real producers emit snake_case, and a scraper should
/// not have to rename its output to satisfy a naming convention. So each field lists the names
/// it answers to, and the first one present wins.
///
/// A converter rather than a naming policy, because several aliases are not case variants at
/// all: a BE FORWARD document calls the listing id `stock_id` and the photo array `images`.
/// A policy could never map those.
///
/// Unknown properties are ignored rather than rejected. A source that adds a field should not
/// break an importer that does not know about it yet, and the whole payload is preserved
/// verbatim anyway, so nothing is lost by not mapping it today.
/// </remarks>
public sealed class VehicleImportRecordConverter : JsonConverter<VehicleImportRecord>
{
    public override VehicleImportRecord Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            // Reported rather than quietly producing an empty record. A bare string or number
            // inside the vehicles array is a producer bug, and an all-null vehicle would be
            // imported as a real car with no data instead of being counted as a failure.
            throw new JsonException(
                $"Each entry in 'vehicles' must be an object; found {root.ValueKind}.");
        }

        return new VehicleImportRecord
        {
            ExternalId = Str(root, "externalId", "stock_id", "stockId", "id", "listingId", "listing_id"),
            ListingUrl = Str(root, "listingUrl", "listing_url", "url"),
            Title = Str(root, "title"),

            Make = Str(root, "make", "brand", "brand_name"),
            Model = Str(root, "model", "model_name"),
            Variant = Str(root, "variant", "trim", "grade"),
            Year = Int(root, "year", "model_year", "modelYear"),

            Mileage = Int(root, "mileage", "odometer"),
            MileageUnit = Str(root, "mileageUnit", "mileage_unit"),
            Steering = Str(root, "steering", "steeringSide", "steering_side"),
            FuelType = Str(root, "fuelType", "fuel_type"),
            Transmission = Str(root, "transmission"),
            Drivetrain = Str(root, "drivetrain", "drive_type", "driveType"),
            BodyType = Str(root, "bodyType", "body_type"),
            EngineCc = Int(root, "engineCc", "engine_cc", "displacement"),
            ExteriorColor = Str(root, "exteriorColor", "exterior_color", "color"),
            Seats = Int(root, "seats", "seat_count", "seatCount"),
            Doors = Int(root, "doors", "door_count", "doorCount"),

            Price = Dec(root, "price", "price_original"),
            Currency = Str(root, "currency", "price_original_currency", "currencyCode"),
            PriceType = Str(root, "priceType", "price_type", "incoterm"),

            Vin = Str(root, "vin"),
            ChassisNumber = Str(root, "chassisNumber", "chassis_number"),

            // NOT chassis_code. That is a model designation - every Toyota Passo of a
            // generation carries M700A - so treating it as a chassis number would merge every
            // car of a model into one vehicle. It is kept as a specification below.
            LotNumber = Str(root, "lotNumber", "lot_number", "stock_id", "stockId"),
            ChassisCode = Str(root, "chassisCode", "chassis_code", "model_code", "modelCode"),

            LocationCountry = Str(root, "locationCountry", "location_country"),
            LocationCity = Str(root, "locationCity", "location_city", "location"),
            PortOfLoading = Str(root, "portOfLoading", "port_of_loading", "port"),
            DestinationMarkets = Strings(root, "destinationMarkets", "destination_markets"),

            ImageUrls = Strings(root, "imageUrls", "image_urls", "images", "photos"),
            ConditionNotes = Str(root, "conditionNotes", "condition_notes", "condition"),

            IsAvailable = Bool(root, "isAvailable", "is_available", "available"),
            LastSeenAtUtc = Date(root, "lastSeenAtUtc", "last_seen_at", "lastSeenAt"),
            FirstSeenAtUtc = Date(root, "firstSeenAtUtc", "first_seen_at", "firstSeenAt"),
        };
    }

    public override void Write(
        Utf8JsonWriter writer, VehicleImportRecord value, JsonSerializerOptions options)
        // Only ever read from a file. Writing would need a chosen spelling, and choosing one
        // would quietly make the other second-class.
        => throw new NotSupportedException(
            "VehicleImportRecord is an input contract and is never serialised.");

    private static bool Find(JsonElement root, string[] names, out JsonElement value)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? Str(JsonElement root, params string[] names)
    {
        if (!Find(root, names, out var value))
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int? Int(JsonElement root, params string[] names)
    {
        if (!Find(root, names, out var value))
        {
            return null;
        }

        // Numbers arrive as numbers or as strings depending on the producer, and a scraper
        // that quotes its integers is not sending a malformed file.
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.Number when value.TryGetDouble(out var d) => (int)d,
            JsonValueKind.String when int.TryParse(value.GetString(), out var s) => s,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var s) => (int)s,
            _ => null,
        };
    }

    private static decimal? Dec(JsonElement root, params string[] names)
    {
        if (!Find(root, names, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var n) => n,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static bool? Bool(JsonElement root, params string[] names)
    {
        if (!Find(root, names, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static DateTime? Date(JsonElement root, params string[] names)
    {
        if (!Find(root, names, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(
                value.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed)
            ? parsed
            : null;
    }

    private static List<string> Strings(JsonElement root, params string[] names)
    {
        if (!Find(root, names, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())];
    }
}
