using System.Text.Json;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Enums;
using CarDealer.Integrations.Carapis;

namespace CarDealer.UnitTests;

/// <summary>
/// Normalization, driven by responses captured from the live API on 2026-08-31.
/// </summary>
/// <remarks>
/// Fixtures rather than hand-written JSON on purpose. Every surprise these tests encode -
/// empty-string VINs, a price_usd of 17,022,000, a registration plate in a field named
/// vehicle_no - came out of a real payload, and a hand-written fixture would have quietly
/// omitted all of them.
/// </remarks>
public class CarapisNormalizerTests
{
    private const long SourceId = 42;

    private readonly CarapisNormalizer _normalizer = new();

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static RawVehicleRecord Record(string json, string externalId = "x", string source = "sbtjapan")
        => new()
        {
            ExternalId = externalId,
            SourceCode = source,
            RawPayload = json,
            RetrievedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        };

    /// <summary>Pulls one element out of a captured list fixture by its <c>_case</c> label.</summary>
    private static string CaseFromList(string startsWith)
    {
        using var doc = JsonDocument.Parse(Fixture("carapis-vehicles-list.json"));
        foreach (var el in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            if (el.TryGetProperty("_case", out var c)
                && c.GetString()!.StartsWith(startsWith, StringComparison.Ordinal))
            {
                return el.GetRawText();
            }
        }

        throw new InvalidOperationException($"No fixture case starting with '{startsWith}'.");
    }

    // --- The trap ------------------------------------------------------------------------

    [Fact]
    public void Empty_string_vin_does_not_become_a_canonical_hash()
    {
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail-sbtjapan.json")), SourceId);

        Assert.NotNull(result);
        Assert.Null(result!.Vehicle.Vin);

        // The hash falls through to the lot number rather than hashing "".
        Assert.Equal($"{SourceId}:AO4106", result.Vehicle.CanonicalHash);
        Assert.Equal(CanonicalHashSource.SourceLotNumber, result.Vehicle.CanonicalHashSource);
    }

    [Fact]
    public void Two_sbt_vehicles_without_vins_get_different_hashes()
    {
        var json = Fixture("carapis-vehicle-detail-sbtjapan.json");
        var first = _normalizer.Normalize(Record(json), SourceId);
        var second = _normalizer.Normalize(Record(json.Replace("AO4106", "AO9999")), SourceId);

        // If blank VINs were hashed, both would carry the identical hash and D3 would merge
        // them - and every other VIN-less vehicle from this source with them.
        Assert.NotEqual(first!.Vehicle.CanonicalHash, second!.Vehicle.CanonicalHash);
    }

    [Fact]
    public void A_registration_plate_is_never_read_as_a_chassis_number()
    {
        // vehicle_no on the Korean record is "277가7312", a plate. It must not reach
        // ChassisNumber, where it would feed dedup rule 2 a value that changes on re-plating.
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail.json")), SourceId);

        Assert.Null(result!.Vehicle.ChassisNumber);
        Assert.Equal("JTNBA1HK9R3039064", result.Vehicle.CanonicalHash);
        Assert.Equal(CanonicalHashSource.Vin, result.Vehicle.CanonicalHashSource);
    }

    // --- Price ---------------------------------------------------------------------------

    [Fact]
    public void Price_comes_from_price_original_and_its_stated_currency()
    {
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail.json")), SourceId);

        // 30,900,000 KRW - not the 22000 in price_usd.
        Assert.Equal(30_900_000m, result!.Listing.Price);
        Assert.Equal("KRW", result.Listing.CurrencyCode);
    }

    [Fact]
    public void An_absurd_price_usd_is_never_adopted_as_a_price()
    {
        // The 2008 Camry priced at 17,022,000 "USD" on opensooq_ye. The list projection has no
        // price_original at all, so the honest outcome is no price rather than that number.
        var result = _normalizer.Normalize(Record(CaseFromList("price_usd is NOT usd: 17,022,000")), SourceId);

        Assert.Null(result!.Listing.Price);
        Assert.Null(result.Listing.CurrencyCode);
    }

    [Fact]
    public void Base_currency_is_left_for_the_fx_step_to_pin()
    {
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail.json")), SourceId);

        // Decision D6 requires a pinned rate. price_usd carries neither rate nor date.
        Assert.Null(result!.Listing.PriceBaseCurrency);
        Assert.Null(result.Listing.ExchangeRateId);
    }

    [Fact]
    public void An_exporter_pricing_in_usd_is_carried_as_usd()
    {
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail-sbtjapan.json")), SourceId);

        // price_original 4290.00 USD, against a rounded price_usd of 4300.
        Assert.Equal(4290m, result!.Listing.Price);
        Assert.Equal("USD", result.Listing.CurrencyCode);
    }

    [Fact]
    public void Incoterm_is_left_unknown_because_the_provider_publishes_none()
    {
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail-sbtjapan.json")), SourceId);

        Assert.Equal(PriceType.Unknown, result!.Listing.PriceType);
    }

    // --- Steering side -------------------------------------------------------------------

    [Fact]
    public void Steering_side_is_recovered_from_the_description_and_flagged_as_inferred()
    {
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail-sbtjapan.json")), SourceId);

        Assert.Equal(SteeringSide.RightHandDrive, result!.Vehicle.SteeringSide);

        // A value read out of prose is not a value the source declared, and the caller is told.
        Assert.Contains("SteeringSide", result.InferredFields);
    }

    [Fact]
    public void Steering_side_stays_unknown_when_the_description_says_nothing()
    {
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail.json")), SourceId);

        Assert.Equal(SteeringSide.Unknown, result!.Vehicle.SteeringSide);
        Assert.DoesNotContain("SteeringSide", result.InferredFields);
    }

    // --- Enumerations and tri-state ------------------------------------------------------

    [Theory]
    [InlineData("gasoline", FuelType.Petrol)]
    [InlineData("plug_hybrid", FuelType.PluginHybrid)]
    [InlineData("hybrid", FuelType.Hybrid)]
    [InlineData("something_new", FuelType.Unknown)]
    public void Fuel_type_maps_and_falls_back_to_unknown(string wire, FuelType expected)
    {
        var json = $$"""{"id":"a","fuel_type":"{{wire}}"}""";

        Assert.Equal(expected, _normalizer.Normalize(Record(json), SourceId)!.Vehicle.FuelType);
    }

    [Fact]
    public void A_null_availability_is_unknown_rather_than_unavailable()
    {
        // is_available is tri-state. "The source did not say" is not "the car is gone".
        var json = """{"id":"a","is_available":null}""";

        Assert.Equal(VehicleStatus.Unknown, _normalizer.Normalize(Record(json), SourceId)!.Vehicle.Status);
    }

    [Fact]
    public void Blank_strings_throughout_become_nulls()
    {
        var json = """{"id":"a","trim":"","brand_name":"","color":"","listing_url":""}""";
        var result = _normalizer.Normalize(Record(json), SourceId);

        Assert.Null(result!.Vehicle.Variant);
        Assert.Null(result.Vehicle.Make);
        Assert.Null(result.Vehicle.ExteriorColor);
        Assert.Null(result.Listing.SourceUrl);
    }

    // --- Provenance and media ------------------------------------------------------------

    [Fact]
    public void The_raw_payload_is_kept_verbatim_for_reprocessing()
    {
        var json = Fixture("carapis-vehicle-detail-sbtjapan.json");
        var result = _normalizer.Normalize(Record(json), SourceId);

        Assert.Equal(json, result!.Listing.RawPayload);
    }

    [Fact]
    public void Relative_photo_paths_are_resolved_against_the_media_base()
    {
        var result = _normalizer.Normalize(
            Record(Fixture("carapis-vehicle-detail-sbtjapan.json")), SourceId, "https://api.carapis.com");

        var main = result!.Images.First();

        Assert.StartsWith("https://api.carapis.com/media/vehicles/", main.ImageUrl);

        // Absolute urls in the same array are left alone.
        Assert.All(result.Images, i => Assert.StartsWith("https://", i.ImageUrl));
    }

    [Fact]
    public void A_record_with_no_media_normalizes_to_no_images()
    {
        var result = _normalizer.Normalize(Record(CaseFromList("no media at all")), SourceId);

        Assert.Empty(result!.Images);
    }

    [Fact]
    public void Catalog_rows_from_a_shared_source_are_global()
    {
        var result = _normalizer.Normalize(Record(Fixture("carapis-vehicle-detail-sbtjapan.json")), SourceId);

        // Null TenantId is decision D1's global catalog. The write guard is what stops a
        // tenant-scoped request path from creating these.
        Assert.Null(result!.Vehicle.TenantId);
        Assert.Null(result.Listing.TenantId);
    }

    [Fact]
    public void Malformed_or_identifierless_payloads_are_skipped_rather_than_guessed_at()
    {
        Assert.Null(_normalizer.Normalize(Record("""{"source_code":"sbtjapan"}"""), SourceId));
        Assert.Null(_normalizer.Normalize(Record("""{"id":""}"""), SourceId));
    }
}
