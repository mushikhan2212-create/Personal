using System.Text;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Enums;
using CarDealer.Integrations.FileImport;

namespace CarDealer.UnitTests;

/// <summary>
/// Reading a real producer's document, in the shape it actually emits.
/// </summary>
/// <remarks>
/// The format was published in camelCase; the scraper that feeds it emits snake_case, with
/// names of its own for several fields - `stock_id` for the listing id, `images` for the photo
/// array. Renaming a working scraper's output to satisfy a naming convention would be the
/// wrong way round, so the importer answers to both.
///
/// The fixture is a trimmed copy of a genuine BE FORWARD capture, including its awkward parts:
/// steering reported as "ASK", drive type as "-", no availability, no incoterm, and a
/// `chassis_code` that names a model rather than a car.
/// </remarks>
public class ImportFormatTests
{
    private const string RealDocument = """
        {
          "sourceCode": "beforward",
          "capturedAtUtc": "2026-09-03T09:02:11Z",
          "vehicles": [
            {
              "stock_id": "CE623595",
              "title": "2022 TOYOTA PASSO 1.0XLPKG",
              "make": "TOYOTA",
              "model": "Passo",
              "year": 2022,
              "price": 8050.0,
              "currency": "USD",
              "mileage": 19137,
              "mileage_unit": "km",
              "engine_cc": 1000,
              "fuel_type": "Petrol",
              "transmission": "CVT",
              "drive_type": "2WD",
              "steering": "Right",
              "body_type": "Hatchback",
              "chassis_code": "M700A",
              "color": "Beige",
              "seats": 5,
              "doors": 5,
              "location": "NAGOYA",
              "listing_url": "https://www.beforward.jp/toyota/passo/ce623595/",
              "images": ["https://img.test/a.jpg", "https://img.test/b.jpg"],
              "condition_notes": null
            },
            {
              "stock_id": "CB639624",
              "title": "2022 TOYOTA PASSO 1.0",
              "make": "TOYOTA",
              "model": "Passo",
              "year": 2022,
              "price": 7450.0,
              "currency": "USD",
              "steering": "ASK",
              "drive_type": "-",
              "chassis_code": "5BA-M700A",
              "images": []
            }
          ]
        }
        """;

    private static IReadOnlyList<RawVehicleRecord> ReadAll(string json, string code = "beforward")
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var provider = JsonFileVehicleProvider.Read(stream, code);

        var page = provider.FetchPageAsync(
            new VehicleSourceQuery { SourceCode = code, PageSize = 100 }).Result;

        return page.Records;
    }

    private static NormalizedVehicle Normalize(RawVehicleRecord record, long sourceId = 7)
        => new ImportNormalizer().Normalize(record, sourceId)
           ?? throw new InvalidOperationException("The record could not be normalized.");

    [Fact]
    public void A_snake_case_document_is_read_without_being_renamed_first()
    {
        var records = ReadAll(RealDocument);

        Assert.Equal(2, records.Count);
        Assert.Equal("CE623595", records[0].ExternalId);

        var car = Normalize(records[0]).Vehicle;

        Assert.Equal("TOYOTA", car.Make);
        Assert.Equal("Passo", car.Model);
        Assert.Equal(2022, car.ModelYear);
        Assert.Equal(19137, car.Mileage);
        Assert.Equal(MileageUnit.Kilometers, car.MileageUnit);
        Assert.Equal(1000, car.EngineDisplacementCc);
        Assert.Equal(FuelType.Petrol, car.FuelType);
        Assert.Equal(Transmission.ContinuouslyVariable, car.Transmission);
        Assert.Equal(SteeringSide.RightHandDrive, car.SteeringSide);
        Assert.Equal("Beige", car.ExteriorColor);
        Assert.Equal((byte)5, car.Seats);
        Assert.Equal((byte)5, car.Doors);
    }

    [Fact]
    public void The_stock_id_identifies_the_car_and_the_chassis_code_never_does()
    {
        var records = ReadAll(RealDocument);

        var first = Normalize(records[0]).Vehicle;
        var second = Normalize(records[1]).Vehicle;

        // Identity comes from the source's stock number, scoped to the source.
        Assert.Equal("CE623595", first.LotNumber);
        Assert.Equal(CanonicalHashSource.SourceLotNumber, first.CanonicalHashSource);

        // chassis_code is a model designation - every Passo of a generation carries M700A - so
        // it is stored as a specification and kept away from identity entirely. Treating it as
        // a chassis number would merge unrelated cars into one.
        Assert.Null(first.ChassisNumber);
        Assert.Null(second.ChassisNumber);
        Assert.Equal("M700A", first.Engine);
        Assert.Equal("5BA-M700A", second.Engine);

        // Two different cars, two different hashes.
        Assert.NotEqual(first.CanonicalHash, second.CanonicalHash);
    }

    [Fact]
    public void Unstated_values_are_Unknown_rather_than_guessed()
    {
        var second = Normalize(ReadAll(RealDocument)[1]);

        // "ASK" means the source declined to say, and "-" the same for drive type. Neither is
        // a value, and neither is a reason to reject the car.
        Assert.Equal(SteeringSide.Unknown, second.Vehicle.SteeringSide);
        Assert.Equal(Drivetrain.Unknown, second.Vehicle.Drivetrain);

        // No availability stated, so Unknown - which search treats as visible, because absence
        // of evidence that a car is gone is not evidence that it is gone.
        Assert.Equal(VehicleStatus.Unknown, second.Vehicle.Status);

        // No incoterm published. FOB and CIF differ by the whole cost of shipping, so it stays
        // unstated rather than being assumed.
        Assert.Equal(PriceType.Unknown, second.Listing.PriceType);
    }

    [Fact]
    public void Freshness_falls_back_to_the_documents_capture_time()
    {
        var records = ReadAll(RealDocument);
        var listing = Normalize(records[0]).Listing;

        // No per-record timestamp, so the document's capturedAtUtc governs: every record in it
        // was seen when it was made. "Now" is never substituted - that would assert a
        // confirmation nobody made.
        Assert.Equal(new DateTime(2026, 9, 3, 9, 2, 11, DateTimeKind.Utc), listing.LastSeenAtUtc);
    }

    [Fact]
    public void A_grade_is_read_out_of_the_title_and_recorded_as_inferred()
    {
        var normalized = Normalize(ReadAll(RealDocument)[0]);

        // BE FORWARD publishes no trim field, but writes it into the title.
        Assert.Equal("1.0XLPKG", normalized.Vehicle.Variant);

        // And says so, so a derived grade is never mistaken for a stated one.
        Assert.Contains(normalized.InferredFields, f => f.StartsWith("variant"));
    }

    [Fact]
    public void The_camel_case_spelling_still_works_unchanged()
    {
        const string camel = """
            {
              "sourceCode": "beforward",
              "vehicles": [
                {
                  "externalId": "X-1",
                  "make": "Nissan",
                  "model": "Note",
                  "mileageUnit": "km",
                  "fuelType": "hybrid",
                  "priceType": "FOB",
                  "imageUrls": ["https://img.test/x.jpg"],
                  "lastSeenAtUtc": "2026-09-01T00:00:00Z"
                }
              ]
            }
            """;

        var normalized = Normalize(ReadAll(camel)[0]);

        Assert.Equal("Nissan", normalized.Vehicle.Make);
        Assert.Equal(MileageUnit.Kilometers, normalized.Vehicle.MileageUnit);
        Assert.Equal(FuelType.Hybrid, normalized.Vehicle.FuelType);
        Assert.Equal(PriceType.FreeOnBoard, normalized.Listing.PriceType);
        Assert.Single(normalized.Images);
    }

    [Fact]
    public void A_document_naming_another_source_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ReadAll(RealDocument, code: "sbtjapan"));

        Assert.Contains("beforward", error.Message);
        Assert.Contains("sbtjapan", error.Message);
    }
}
