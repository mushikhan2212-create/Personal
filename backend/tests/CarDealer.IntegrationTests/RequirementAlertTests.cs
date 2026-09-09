using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Telling a salesperson when a car they are waiting for turns up (open item O11).
/// </summary>
/// <remarks>
/// The behaviour these exist to pin down is not "a match raises an alert" - it is the two ways
/// the feature becomes useless. Alert on the catalogue rather than on new stock and a fresh
/// requirement fires forty notifications about cars already on its own matches list. Alert
/// again on every scan and the inbox fills with the same car every hour. Either one and the
/// salesperson stops reading alerts, which is worse than not having them.
/// </remarks>
public sealed class RequirementAlertTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RequirementAlertTests(ApiFactory factory) => _factory = factory;

    /// <summary>Letters-only marker, for the reason recorded in <see cref="SearchTextTests"/>.</summary>
    private static string Marker() => "AL" + new string([.. Guid.NewGuid().ToByteArray().Take(8)
        .Select(b => (char)('a' + (b % 16)))]);

    /// <summary>Puts one car in the shared catalogue, listed as first seen at a given moment.</summary>
    private async Task AddVehicleAsync(string marker, string model, DateTime firstSeenUtc)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var source = await db.VehicleSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Code == "alert-test-source");

        if (source is null)
        {
            source = new VehicleSource
            {
                Name = "Alert test source",
                Code = "alert-test-source",
                ProviderType = VehicleSourceProviderType.DealerJson,
                SourceType = VehicleSourceType.File,
                IsShared = true,
            };

            db.VehicleSources.Add(source);
            await db.SaveChangesAsync();
        }

        var vehicle = new Vehicle
        {
            PublicId = Guid.NewGuid(),
            Make = $"{marker}Toyota",
            Model = model,
            ModelYear = 2019,
            Status = VehicleStatus.Active,
        };

        db.Vehicles.Add(vehicle);
        db.VehicleListings.Add(new VehicleListing
        {
            Vehicle = vehicle,
            VehicleSourceId = source.Id,
            ExternalListingId = $"{marker}-{Guid.NewGuid():N}"[..24],
            Price = 6_000m,
            CurrencyCode = "USD",
            PriceBaseCurrency = 6_000m,
            BaseCurrencyCode = "USD",
            PriceType = PriceType.FreeOnBoard,

            // The field the whole feature turns on: whether this car existed before the
            // customer asked, or arrived afterwards.
            FirstSeenAtUtc = firstSeenUtc,
            LastSeenAtUtc = DateTime.UtcNow,
            IsActive = true,
        });

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddCustomerAsync(HttpClient client, string marker)
    {
        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = $"{marker}Alert",
            lastName = "Tester",
            phone = $"+81 90 {Guid.NewGuid().ToString("N")[..4]} 0000",
        });

        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();
    }

    private static async Task AddRequirementAsync(HttpClient client, Guid customer, string marker)
    {
        var added = await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/requirements",
            new { name = "Waiting for one", make = $"{marker}Toyota" });

        added.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ScanAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/v1/alerts/scan", null);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<JsonElement>> AlertsForAsync(HttpClient client, string marker)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/alerts?pageSize=100");

        return [.. list.GetProperty("items").EnumerateArray().Where(a =>
            a.GetProperty("customer").GetProperty("firstName").GetString() == $"{marker}Alert")];
    }

    [Fact]
    public async Task A_car_that_arrives_after_the_requirement_raises_one_alert()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(client, m);
        await AddRequirementAsync(client, customer, m);

        // Listed a minute from now, so it is unambiguously after the requirement whatever the
        // clock did between the two calls.
        await AddVehicleAsync(m, "Hiace", DateTime.UtcNow.AddMinutes(1));

        await ScanAsync(client);

        var alerts = await AlertsForAsync(client, m);

        Assert.Single(alerts);
        Assert.Equal("Hiace", alerts[0].GetProperty("vehicle").GetProperty("model").GetString());

        // The price is copied at match time, so a later price change cannot rewrite what the
        // salesperson was told.
        Assert.Equal(6_000m, alerts[0].GetProperty("priceBaseAtMatch").GetDecimal());
    }

    [Fact]
    public async Task Stock_that_was_already_there_raises_nothing()
    {
        // The rule that separates an alert from a search result. Without it, writing a
        // requirement against a stocked catalogue produces an alert per matching car - dozens
        // of notifications about cars on the requirement's own matches list.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        await AddVehicleAsync(m, "Corolla", DateTime.UtcNow.AddDays(-3));

        var customer = await AddCustomerAsync(client, m);
        await AddRequirementAsync(client, customer, m);

        await ScanAsync(client);

        Assert.Empty(await AlertsForAsync(client, m));
    }

    [Fact]
    public async Task Scanning_twice_does_not_alert_twice()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(client, m);
        await AddRequirementAsync(client, customer, m);
        await AddVehicleAsync(m, "Hiace", DateTime.UtcNow.AddMinutes(1));

        var first = await ScanAsync(client);
        var second = await ScanAsync(client);

        Assert.True(first.GetProperty("alertsRaised").GetInt32() >= 1);

        // The scan runs hourly and can be triggered by hand, so the second run has to be a
        // no-op rather than something anyone has to be careful about.
        Assert.Equal(0, second.GetProperty("alertsRaised").GetInt32());
        Assert.Single(await AlertsForAsync(client, m));
    }

    [Fact]
    public async Task A_fulfilled_requirement_stops_alerting()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(client, m);

        var added = await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/requirements",
            new { name = "Already sorted", make = $"{m}Toyota", status = "Fulfilled" });
        added.EnsureSuccessStatusCode();

        await AddVehicleAsync(m, "Hiace", DateTime.UtcNow.AddMinutes(1));
        await ScanAsync(client);

        // Only open requirements are scanned. Telling someone about stock for a customer who
        // has already bought is how an inbox stops being read.
        Assert.Empty(await AlertsForAsync(client, m));
    }

    [Fact]
    public async Task Marking_an_alert_seen_takes_it_out_of_the_unseen_count()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(client, m);
        await AddRequirementAsync(client, customer, m);
        await AddVehicleAsync(m, "Hiace", DateTime.UtcNow.AddMinutes(1));
        await ScanAsync(client);

        var alerts = await AlertsForAsync(client, m);
        var id = alerts[0].GetProperty("id").GetInt64();

        var before = (await client.GetFromJsonAsync<JsonElement>("/api/v1/alerts/count"))
            .GetProperty("unseen").GetInt32();

        var marked = await client.PostAsync($"/api/v1/alerts/{id}/seen", null);
        marked.EnsureSuccessStatusCode();

        var after = (await client.GetFromJsonAsync<JsonElement>("/api/v1/alerts/count"))
            .GetProperty("unseen").GetInt32();

        Assert.Equal(before - 1, after);
    }

    [Fact]
    public async Task One_tenants_alerts_are_invisible_to_another()
    {
        var m = Marker();
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(nihon, m);
        await AddRequirementAsync(nihon, customer, m);
        await AddVehicleAsync(m, "Hiace", DateTime.UtcNow.AddMinutes(1));
        await ScanAsync(nihon);

        var mine = await AlertsForAsync(nihon, m);
        Assert.Single(mine);

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        Assert.Empty(await AlertsForAsync(karachi, m));

        // And the other tenant cannot clear it either - a 404 rather than a 403, because
        // confirming the id exists would leak that somebody has a customer waiting for a car.
        var id = mine[0].GetProperty("id").GetInt64();
        var response = await karachi.PostAsync($"/api/v1/alerts/{id}/seen", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var stillUnseen = await db.RequirementAlerts
            .IgnoreQueryFilters()
            .Where(a => a.Id == id)
            .Select(a => a.SeenAtUtc)
            .FirstAsync();

        Assert.Null(stillUnseen);
    }

    [Fact]
    public async Task Read_only_users_can_see_alerts_but_not_clear_them()
    {
        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var count = await readOnly.GetAsync("/api/v1/alerts/count");
        Assert.Equal(HttpStatusCode.OK, count.StatusCode);

        // Clearing an alert in a shared inbox tells colleagues it has been dealt with, which
        // is not a read.
        var cleared = await readOnly.PostAsync("/api/v1/alerts/seen", null);
        Assert.Equal(HttpStatusCode.Forbidden, cleared.StatusCode);

        var scanned = await readOnly.PostAsync("/api/v1/alerts/scan", null);
        Assert.Equal(HttpStatusCode.Forbidden, scanned.StatusCode);
    }
}
