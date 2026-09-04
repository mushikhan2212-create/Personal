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
/// Deleting a vehicle source and the data only it was holding up.
/// </summary>
/// <remarks>
/// The rule these pin down is the one a user would be most upset to discover afterwards: a car
/// another source also lists survives. Deleting BE FORWARD must not silently remove vehicles
/// SBT is still selling.
/// </remarks>
public sealed class SourceDeletionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SourceDeletionTests(ApiFactory factory) => _factory = factory;

    private static MultipartFormDataContent Upload(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return new MultipartFormDataContent { { content, "file", "import.json" } };
    }

    private async Task<string> SourceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var code = $"del-{Guid.NewGuid():N}"[..14];

        db.VehicleSources.Add(new VehicleSource
        {
            Name = "Deletion test source",
            Code = code,
            ProviderType = VehicleSourceProviderType.DealerJson,
            SourceType = VehicleSourceType.File,
            IsShared = true,
        });

        await db.SaveChangesAsync();
        return code;
    }

    private static string Document(params object[] vehicles)
        => JsonSerializer.Serialize(new { vehicles });

    private static object Car(string id, string make, string model, string? vin = null)
        => new
        {
            externalId = id,
            make,
            model,
            year = 2019,
            price = 1_000_000,
            currency = "JPY",
            vin,
            imageUrls = new[] { $"https://img.test/{id}.jpg" },
            isAvailable = true,
            lastSeenAtUtc = DateTime.UtcNow,
        };

    [Fact]
    public async Task Deleting_a_source_removes_its_listings_and_the_cars_only_it_offered()
    {
        var code = await SourceAsync();
        var marker = $"DEL{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        await client.PostAsync($"/api/v1/vehicle-sources/{code}/import", Upload(Document(
            Car("d-1", marker, "Hiace"),
            Car("d-2", marker, "Prado"))));

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(2, before.GetProperty("totalCount").GetInt32());

        var response = await client.DeleteAsync($"/api/v1/vehicle-sources/{code}?confirm={code}");
        response.EnsureSuccessStatusCode();

        var outcome = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, outcome.GetProperty("listingsDeleted").GetInt32());
        Assert.Equal(2, outcome.GetProperty("vehiclesDeleted").GetInt32());
        Assert.Equal(0, outcome.GetProperty("vehiclesKept").GetInt32());

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(0, after.GetProperty("totalCount").GetInt32());

        // The source itself is gone, not merely emptied.
        var sources = await client.GetFromJsonAsync<JsonElement>("/api/v1/vehicle-sources");
        Assert.DoesNotContain(
            sources.EnumerateArray(), s => s.GetProperty("code").GetString() == code);
    }

    [Fact]
    public async Task A_car_another_source_still_lists_survives_the_deletion()
    {
        var first = await SourceAsync();
        var second = await SourceAsync();
        var marker = $"DEL{Guid.NewGuid():N}"[..10];
        var shared = $"JT{Guid.NewGuid():N}"[..17];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // One car both sources offer, and one only the first has.
        await client.PostAsync($"/api/v1/vehicle-sources/{first}/import", Upload(Document(
            Car("s-1", marker, "Prado", shared),
            Car("s-2", marker, "Hiace"))));

        await client.PostAsync($"/api/v1/vehicle-sources/{second}/import", Upload(Document(
            Car("s-3", marker, "Prado", shared))));

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(2, before.GetProperty("totalCount").GetInt32());

        var response = await client.DeleteAsync($"/api/v1/vehicle-sources/{first}?confirm={first}");
        var outcome = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The Prado is kept because the second source still lists it; only the Hiace goes.
        Assert.Equal(2, outcome.GetProperty("listingsDeleted").GetInt32());
        Assert.Equal(1, outcome.GetProperty("vehiclesDeleted").GetInt32());
        Assert.Equal(1, outcome.GetProperty("vehiclesKept").GetInt32());

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(1, after.GetProperty("totalCount").GetInt32());
        Assert.Equal("Prado", after.GetProperty("items")[0].GetProperty("model").GetString());

        // And it is down to one offer, from the surviving source.
        Assert.Equal(1, after.GetProperty("items")[0].GetProperty("offerCount").GetInt32());
    }

    [Fact]
    public async Task Deleting_without_repeating_the_code_is_refused()
    {
        var code = await SourceAsync();
        var marker = $"DEL{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        await client.PostAsync($"/api/v1/vehicle-sources/{code}/import",
            Upload(Document(Car("c-1", marker, "Hiace"))));

        foreach (var attempt in new[] { "", "?confirm=", "?confirm=something-else" })
        {
            var response = await client.DeleteAsync($"/api/v1/vehicle-sources/{code}{attempt}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Nothing was destroyed by the refused attempts.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(1, after.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Deleting_takes_the_sync_history_and_tenant_overlay_with_it()
    {
        var code = await SourceAsync();
        var marker = $"DEL{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        await client.PostAsync($"/api/v1/vehicle-sources/{code}/import",
            Upload(Document(Car("h-1", marker, "Canter"))));

        long vehicleId;
        long sourceId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
            var source = await db.VehicleSources.IgnoreQueryFilters().FirstAsync(s => s.Code == code);
            sourceId = source.Id;

            vehicleId = await db.VehicleListings.IgnoreQueryFilters()
                .Where(l => l.VehicleSourceId == sourceId).Select(l => l.VehicleId).FirstAsync();

            var tenantId = await db.Tenants.OrderBy(t => t.Id).Select(t => t.Id).FirstAsync();

            db.TenantVehicles.Add(new TenantVehicle
            {
                TenantId = tenantId,
                VehicleId = vehicleId,
                TenantPrice = 9_999m,
                TenantCurrencyCode = "USD",
            });
            await db.SaveChangesAsync();
        }

        var response = await client.DeleteAsync($"/api/v1/vehicle-sources/{code}?confirm={code}");
        var outcome = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, outcome.GetProperty("tenantOverlaysDeleted").GetInt32());
        Assert.True(outcome.GetProperty("syncJobsDeleted").GetInt32() >= 1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            // Every dependent row is gone. A restricting foreign key left behind would have
            // made the delete throw rather than leave an orphan, but asserting it directly
            // documents which tables the deletion is responsible for.
            Assert.False(await db.SyncJobs.IgnoreQueryFilters().AnyAsync(j => j.VehicleSourceId == sourceId));
            Assert.False(await db.TenantVehicles.IgnoreQueryFilters().AnyAsync(o => o.VehicleId == vehicleId));
            Assert.False(await db.VehicleImages.IgnoreQueryFilters().AnyAsync(i => i.VehicleId == vehicleId));
            Assert.False(await db.Vehicles.IgnoreQueryFilters().AnyAsync(v => v.Id == vehicleId));
        }
    }

    [Fact]
    public async Task An_unknown_code_is_a_404_and_deleting_needs_the_sync_permission()
    {
        var owner = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.DeleteAsync("/api/v1/vehicle-sources/nope?confirm=nope")).StatusCode);

        var code = await SourceAsync();
        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await readOnly.DeleteAsync($"/api/v1/vehicle-sources/{code}?confirm={code}")).StatusCode);
    }
}
