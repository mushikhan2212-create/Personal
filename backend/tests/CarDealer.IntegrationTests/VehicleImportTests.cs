using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The JSON import path, end to end over HTTP.
/// </summary>
/// <remarks>
/// Import exists because Carapis is a one-shot crawl - every record it returns has
/// first_seen_at equal to last_seen_at, so availability is frozen at capture and stale stock
/// never disappears. Decision D13 records why the answer is to accept data rather than to
/// scrape for it.
///
/// These tests go through the controller rather than the service, because the two defects this
/// path could plausibly reintroduce both live above the service: the global-catalog write
/// guard refusing a tenant-scoped write, and permissions.
/// </remarks>
public sealed class VehicleImportTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public VehicleImportTests(ApiFactory factory) => _factory = factory;

    private async Task<string> RegisterSourceAsync(string? ingestionFilterJson = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var code = $"imp-{Guid.NewGuid():N}"[..16];

        db.VehicleSources.Add(new VehicleSource
        {
            TenantId = null,
            Name = "Import test source",
            Code = code,
            ProviderType = VehicleSourceProviderType.DealerJson,
            SourceType = VehicleSourceType.File,
            IsShared = true,
            IngestionFilterJson = ingestionFilterJson,
        });

        await db.SaveChangesAsync();
        return code;
    }

    private static string Document(string marker, params object[] vehicles)
        => JsonSerializer.Serialize(new
        {
            capturedAtUtc = DateTime.UtcNow,
            vehicles,
        });

    private static object Vehicle(
        string externalId,
        string make,
        string model,
        string? vin = null,
        int year = 2018,
        DateTime? lastSeen = null,
        string[]? destinations = null)
        => new
        {
            externalId,
            make,
            model,
            year,
            mileage = 80000,
            mileageUnit = "km",
            steering = "rhd",
            fuelType = "diesel",
            transmission = "automatic",
            price = 1_420_000,
            currency = "JPY",
            priceType = "FOB",
            vin,
            destinationMarkets = destinations ?? [],
            isAvailable = true,
            lastSeenAtUtc = lastSeen ?? DateTime.UtcNow,
        };

    private static MultipartFormDataContent Upload(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return new MultipartFormDataContent { { content, "file", "import.json" } };
    }

    private async Task<JsonElement> ImportAsync(
        HttpClient client, string code, string json, bool dryRun = false)
    {
        var response = await client.PostAsync(
            $"/api/v1/vehicle-sources/{code}/import?dryRun={dryRun}", Upload(json));

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task An_imported_document_lands_in_the_catalog_and_is_searchable()
    {
        var code = await RegisterSourceAsync();
        var marker = $"IMP{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var body = await ImportAsync(client, code, Document(marker,
            Vehicle("a-1", $"{marker}Toyota", "Hiace Van", vin: $"JT{Guid.NewGuid():N}"[..17]),
            Vehicle("a-2", $"{marker}Toyota", "Land Cruiser Prado")));

        Assert.Equal("Succeeded", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("created").GetInt32());

        // The write went to the GLOBAL catalog from a tenant-authenticated request, which is
        // only possible because the endpoint runs the sync in an unscoped DI scope.
        var found = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/vehicles?q={marker}Toyota");

        Assert.Equal(2, found.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Re_importing_the_same_document_updates_rather_than_duplicates()
    {
        var code = await RegisterSourceAsync();
        var marker = $"IMP{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var vin = $"JT{Guid.NewGuid():N}"[..17];
        var json = Document(marker, Vehicle("b-1", $"{marker}Nissan", "X-Trail", vin: vin));

        var first = await ImportAsync(client, code, json);
        var second = await ImportAsync(client, code, json);

        Assert.Equal(1, first.GetProperty("created").GetInt32());

        // Same VIN, so the second pass matches the existing vehicle instead of adding one.
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(1, second.GetProperty("updated").GetInt32() + second.GetProperty("autoMerged").GetInt32());

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}Nissan");
        Assert.Equal(1, found.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_record_with_no_strong_identifier_is_imported_and_counted()
    {
        var code = await RegisterSourceAsync();
        var marker = $"IMP{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // Japanese export stock frequently has no VIN. It must still be catalogued - the count
        // is what tells the POC report how much of the data dedup cannot help with.
        var body = await ImportAsync(client, code, Document(marker,
            Vehicle("c-1", $"{marker}Honda", "Fit")));

        Assert.Equal(1, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("withoutStrongIdentifier").GetInt32());
    }

    [Fact]
    public async Task A_record_with_no_timestamp_takes_the_documents_capture_time()
    {
        var code = await RegisterSourceAsync();
        var marker = $"IMP{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var captured = new DateTime(2026, 8, 20, 6, 30, 0, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(new
        {
            capturedAtUtc = captured,
            vehicles = new object[]
            {
                // No lastSeenAtUtc. Producers rarely stamp each row, and the document already
                // answers the question: every record in it was seen when it was made.
                new { externalId = "d-2", make = $"{marker}Toyota", model = "Coaster" },
            },
        });

        var body = await ImportAsync(client, code, json);

        Assert.Equal(1, body.GetProperty("created").GetInt32());
        Assert.Equal(0, body.GetProperty("failed").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var listing = await db.VehicleListings.IgnoreQueryFilters()
            .FirstAsync(l => l.ExternalListingId == "d-2");

        // The capture time, not "now". Substituting the import moment would assert a
        // confirmation nobody made - the false freshness that made the previous source useless.
        Assert.Equal(captured, listing.LastSeenAtUtc);
    }

    [Fact]
    public async Task A_malformed_record_fails_alone_and_the_run_continues()
    {
        var code = await RegisterSourceAsync();
        var marker = $"IMP{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var json = $$"""
            {
              "vehicles": [
                {
                  "externalId": "ok-1",
                  "make": "{{marker}}Toyota",
                  "model": "Hiace",
                  "lastSeenAtUtc": "2026-09-01T00:00:00Z"
                },
                "this is not a vehicle object"
              ]
            }
            """;

        var body = await ImportAsync(client, code, json);

        // One bad entry must not discard the good one, and must be reported rather than
        // silently skipped.
        Assert.Equal(1, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        Assert.Equal("PartiallySucceeded", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Records_outside_the_coverage_filter_are_skipped_and_counted()
    {
        var marker = $"IMP{Guid.NewGuid():N}"[..10];

        var code = await RegisterSourceAsync(JsonSerializer.Serialize(new
        {
            makes = new[] { $"{marker}Toyota" },
            models = new[] { "Hiace" },
            minYear = 2015,
        }));

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var body = await ImportAsync(client, code, Document(marker,
            Vehicle("e-1", $"{marker}Toyota", "Hiace Van", year: 2018),
            Vehicle("e-2", $"{marker}Nissan", "Hiace Van", year: 2018),
            Vehicle("e-3", $"{marker}Toyota", "Corolla", year: 2018),
            Vehicle("e-4", $"{marker}Toyota", "Hiace Van", year: 2011)));

        // Only the first passes make, model and year together.
        Assert.Equal(1, body.GetProperty("created").GetInt32());

        // Reported, not silently dropped: a filter that discards three quarters of a file must
        // say so, or it is indistinguishable from a file that was mostly empty.
        Assert.Equal(3, body.GetProperty("skippedOutOfScope").GetInt32());
    }

    [Fact]
    public async Task The_destination_filter_admits_a_listing_that_states_a_permitted_market()
    {
        var marker = $"IMP{Guid.NewGuid():N}"[..10];

        var code = await RegisterSourceAsync(JsonSerializer.Serialize(new
        {
            destinationMarkets = new[] { "PK" },
        }));

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var body = await ImportAsync(client, code, Document(marker,
            Vehicle("f-1", $"{marker}Toyota", "Hiace", destinations: ["PK", "KE"]),
            Vehicle("f-2", $"{marker}Toyota", "Hiace", destinations: ["JP"]),

            // States no destination at all. Admitted: a source not saying where a car can go
            // is not the same as saying it cannot go there.
            Vehicle("f-3", $"{marker}Toyota", "Hiace")));

        Assert.Equal(2, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("skippedOutOfScope").GetInt32());
    }

    [Fact]
    public async Task A_dry_run_reports_what_would_happen_and_writes_nothing()
    {
        var code = await RegisterSourceAsync();
        var marker = $"IMP{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var json = Document(marker, Vehicle("g-1", $"{marker}Toyota", "Hiace"));

        var dry = await ImportAsync(client, code, json, dryRun: true);

        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, dry.GetProperty("created").GetInt32());

        // No SyncJob row, because a run that wrote nothing did not happen - and a job row would
        // corrupt the "last synced" time on the sources screen.
        Assert.Equal(0, dry.GetProperty("syncJobId").GetInt64());

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}Toyota");
        Assert.Equal(0, found.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Malformed_json_is_rejected_whole_without_starting_a_job()
    {
        var code = await RegisterSourceAsync();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsync(
            $"/api/v1/vehicle-sources/{code}/import", Upload("{ this is not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var source = await db.VehicleSources.IgnoreQueryFilters().FirstAsync(s => s.Code == code);

        Assert.False(await db.SyncJobs.IgnoreQueryFilters().AnyAsync(j => j.VehicleSourceId == source.Id));
    }

    [Fact]
    public async Task A_document_naming_a_different_source_is_refused()
    {
        var code = await RegisterSourceAsync();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var json = JsonSerializer.Serialize(new
        {
            sourceCode = "some-other-source",
            vehicles = new[] { Vehicle("h-1", "Toyota", "Hiace") },
        });

        // Importing one exporter's stock under another's misattributes every car, and
        // attribution is a POC acceptance criterion.
        var response = await client.PostAsync($"/api/v1/vehicle-sources/{code}/import", Upload(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Importing_requires_the_sync_permission()
    {
        var code = await RegisterSourceAsync();
        var client = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var json = Document("X", Vehicle("i-1", "Toyota", "Hiace"));
        var response = await client.PostAsync($"/api/v1/vehicle-sources/{code}/import", Upload(json));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_source_code_is_a_404()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var json = Document("X", Vehicle("j-1", "Toyota", "Hiace"));
        var response = await client.PostAsync("/api/v1/vehicle-sources/not-a-source/import", Upload(json));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
