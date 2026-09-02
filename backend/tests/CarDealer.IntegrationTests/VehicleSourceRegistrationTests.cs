using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Registering a vehicle source, and refusing an import a source cannot read.
/// </summary>
/// <remarks>
/// Both exist because the import path shipped without a usable code. The seeded sources are
/// all Carapis, the sync pipeline resolves its normalizer from the source's provider type, and
/// nothing in the API created a source - so every value of {code} either did not exist or
/// resolved an adapter that rejected the whole file and blamed the records for it.
/// </remarks>
public sealed class VehicleSourceRegistrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public VehicleSourceRegistrationTests(ApiFactory factory) => _factory = factory;

    private static string NewCode() => $"src-{Guid.NewGuid():N}"[..16];

    private static MultipartFormDataContent Upload(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return new MultipartFormDataContent { { content, "file", "import.json" } };
    }

    private static string DocumentWith(string marker) => JsonSerializer.Serialize(new
    {
        vehicles = new[]
        {
            new
            {
                externalId = "x-1",
                make = marker,
                model = "Hiace",
                year = 2018,
                lastSeenAtUtc = DateTime.UtcNow,
            },
        },
    });

    [Fact]
    public async Task A_registered_source_can_be_imported_to_immediately()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var code = NewCode();
        var marker = $"REG{Guid.NewGuid():N}"[..10];

        var created = await client.PostAsJsonAsync("/api/v1/vehicle-sources", new
        {
            code,
            name = "BE FORWARD",
            providerType = "DealerJson",
            sourceType = "File",
            isShared = true,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The whole point: the code this returns is one the import endpoint accepts.
        var imported = await client.PostAsync(
            $"/api/v1/vehicle-sources/{code}/import", Upload(DocumentWith(marker)));

        imported.EnsureSuccessStatusCode();
        var body = await imported.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("created").GetInt32());

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(1, found.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Importing_to_a_carapis_source_is_refused_with_the_real_reason()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // sbtjapan is seeded as Carapis. Before this guard the run reported every record as
        // "Payload carried no usable identifier", which named the wrong culprit entirely.
        var response = await client.PostAsync(
            "/api/v1/vehicle-sources/sbtjapan/import", Upload(DocumentWith("X")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var detail = problem.GetProperty("detail").GetString();

        Assert.Contains("Carapis", detail);
        Assert.Contains("DealerJson", detail);

        // And it stopped before starting a run, so nothing pollutes the sync history.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var source = await db.VehicleSources.IgnoreQueryFilters().FirstAsync(s => s.Code == "sbtjapan");

        Assert.False(await db.SyncJobs.IgnoreQueryFilters().AnyAsync(j => j.VehicleSourceId == source.Id));
    }

    [Fact]
    public async Task The_seeded_file_import_source_accepts_an_import_out_of_the_box()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var marker = $"SEED{Guid.NewGuid():N}"[..10];

        // The documented curl commands name a code. One has to exist on a fresh database, or
        // the first thing a reader does fails.
        var response = await client.PostAsync(
            "/api/v1/vehicle-sources/file-import/import?dryRun=true", Upload(DocumentWith(marker)));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task A_duplicate_code_is_a_conflict_rather_than_a_constraint_violation()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var code = NewCode();

        var body = new { code, name = "First", providerType = "DealerJson", isShared = true };

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/v1/vehicle-sources", body)).StatusCode);

        // The unique index over (TenantScope, Code) would otherwise surface as a 500.
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/v1/vehicle-sources", body)).StatusCode);
    }

    [Fact]
    public async Task A_malformed_code_is_rejected_before_it_reaches_a_url()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        foreach (var bad in new[] { "Has Spaces", "UPPER", "trailing/slash", "" })
        {
            var response = await client.PostAsJsonAsync("/api/v1/vehicle-sources", new
            {
                code = bad,
                name = "Bad",
                providerType = "DealerJson",
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task An_unreadable_ingestion_filter_is_rejected_at_registration()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // Caught now rather than at the first import, which is the worst moment to find out
        // that the quota guarding the catalog cannot be read.
        var response = await client.PostAsJsonAsync("/api/v1/vehicle-sources", new
        {
            code = NewCode(),
            name = "Bad filter",
            providerType = "DealerJson",
            ingestionFilterJson = "{ not json",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_tenant_owned_source_keeps_its_vehicles_private()
    {
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var code = NewCode();
        var marker = $"PRIV{Guid.NewGuid():N}"[..10];

        await nihon.PostAsJsonAsync("/api/v1/vehicle-sources", new
        {
            code,
            name = "Nihon's own stock",
            providerType = "DealerJson",

            // Not shared: this is one dealer's inventory, not the global catalog (decision D1).
            isShared = false,
        });

        await nihon.PostAsync($"/api/v1/vehicle-sources/{code}/import", Upload(DocumentWith(marker)));

        var mine = await nihon.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(1, mine.GetProperty("totalCount").GetInt32());

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");
        var theirs = await karachi.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");

        Assert.Equal(0, theirs.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Registering_a_source_requires_the_sync_permission()
    {
        var client = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var response = await client.PostAsJsonAsync("/api/v1/vehicle-sources", new
        {
            code = NewCode(),
            name = "Nope",
            providerType = "DealerJson",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
