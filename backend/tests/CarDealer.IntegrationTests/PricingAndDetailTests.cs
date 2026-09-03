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
/// Base-currency pricing (decision D6) and the vehicle detail endpoint.
/// </summary>
/// <remarks>
/// Both existed as gaps rather than bugs. Nothing populated PriceBaseCurrency, so price range
/// filters matched nothing and both price sorts collapsed every row to the same key - two of
/// the five sort options in the UI were decorative. And there was no detail endpoint at all,
/// so stored images and multiple listings per vehicle were unreachable.
/// </remarks>
public sealed class PricingAndDetailTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PricingAndDetailTests(ApiFactory factory) => _factory = factory;

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

        var code = $"px-{Guid.NewGuid():N}"[..14];

        db.VehicleSources.Add(new VehicleSource
        {
            Name = "Pricing test source",
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

    private static object Car(
        string id, string make, string model, decimal price, string currency,
        string? vin = null, string[]? images = null)
        => new
        {
            externalId = id,
            make,
            model,
            year = 2018,
            mileage = 70000,
            mileageUnit = "km",
            steering = "rhd",
            price,
            currency,
            priceType = "FOB",
            vin,
            imageUrls = images ?? [],
            isAvailable = true,
            lastSeenAtUtc = DateTime.UtcNow,
        };

    [Fact]
    public async Task A_price_is_converted_to_the_base_currency_and_pinned_to_a_rate()
    {
        var code = await SourceAsync();
        var marker = $"FX{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // 1,500,000 JPY at the seeded 150.00 JPY/USD is 10,000 USD.
        await client.PostAsync($"/api/v1/vehicle-sources/{code}/import",
            Upload(Document(Car("fx-1", marker, "Hiace", 1_500_000m, "JPY"))));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var listing = await db.VehicleListings
            .IgnoreQueryFilters()
            .FirstAsync(l => l.ExternalListingId == "fx-1");

        Assert.Equal(10_000m, listing.PriceBaseCurrency);
        Assert.Equal("USD", listing.BaseCurrencyCode);

        // The rate id is the point of D6: the number can be explained later by the exact row
        // that produced it, and re-running a report next year gives the same answer.
        Assert.NotNull(listing.ExchangeRateId);

        var rate = await db.ExchangeRates.FirstAsync(r => r.Id == listing.ExchangeRateId);
        Assert.Equal("JPY", rate.QuoteCurrencyCode.Trim());
    }

    [Fact]
    public async Task A_currency_with_no_rate_keeps_a_null_base_price_and_stays_out_of_range_filters()
    {
        var code = await SourceAsync();
        var marker = $"FX{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // No rate is seeded for Zambian kwacha.
        await client.PostAsync($"/api/v1/vehicle-sources/{code}/import",
            Upload(Document(Car("fx-2", marker, "Canter", 250_000m, "ZMW"))));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var listing = await db.VehicleListings.IgnoreQueryFilters()
            .FirstAsync(l => l.ExternalListingId == "fx-2");

        // Null, not converted at a guessed rate. A price that cannot be compared is not a
        // cheap price, and D6 is explicit that it stays out of range filters.
        Assert.Null(listing.PriceBaseCurrency);
        Assert.Null(listing.ExchangeRateId);

        var unfiltered = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(1, unfiltered.GetProperty("totalCount").GetInt32());

        var ranged = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/vehicles?q={marker}&minPrice=1&maxPrice=1000000");
        Assert.Equal(0, ranged.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Price_range_filters_and_price_sorting_work_on_converted_prices()
    {
        var code = await SourceAsync();
        var marker = $"FX{Guid.NewGuid():N}"[..10];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        await client.PostAsync($"/api/v1/vehicle-sources/{code}/import", Upload(Document(
            Car("fx-lo", marker, "Vitz", 450_000m, "JPY"),      // 3,000 USD
            Car("fx-mid", marker, "Fit", 1_500_000m, "JPY"),    // 10,000 USD
            Car("fx-hi", marker, "Prado", 3_000_000m, "JPY")))); // 20,000 USD

        var mid = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/vehicles?q={marker}&minPrice=5000&maxPrice=15000");

        Assert.Equal(1, mid.GetProperty("totalCount").GetInt32());
        Assert.Equal("Fit", mid.GetProperty("items")[0].GetProperty("model").GetString());

        var ascending = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/vehicles?q={marker}&sort=PriceAscending");

        var models = ascending.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("model").GetString() ?? string.Empty).ToArray();

        Assert.Equal(new[] { "Vitz", "Fit", "Prado" }, models);
    }

    [Fact]
    public async Task Detail_returns_every_image_and_every_listing_offering_the_vehicle()
    {
        var first = await SourceAsync();
        var second = await SourceAsync();
        var marker = $"DT{Guid.NewGuid():N}"[..10];
        var vin = $"JT{Guid.NewGuid():N}"[..17];
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        await client.PostAsync($"/api/v1/vehicle-sources/{first}/import", Upload(Document(
            Car("d-1", marker, "Prado", 2_100_000m, "JPY", vin,
                ["https://img.test/a.jpg", "https://img.test/b.jpg"]))));

        // The same physical car, offered by a second source at a different price. It merges on
        // the VIN, and the detail view has to show both offers or the merge is unauditable.
        await client.PostAsync($"/api/v1/vehicle-sources/{second}/import", Upload(Document(
            Car("d-2", marker, "Prado", 2_250_000m, "JPY", vin))));

        // One card, not two: the same physical car offered twice must not appear twice, or
        // deduplication is invisible exactly when it has worked.
        var search = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(1, search.GetProperty("totalCount").GetInt32());

        var card = search.GetProperty("items")[0];
        Assert.Equal(2, card.GetProperty("offerCount").GetInt32());
        Assert.Equal(2, card.GetProperty("sourceCount").GetInt32());

        // The card shows the cheaper of the two offers, which is the one a buyer would act on.
        Assert.Equal(14_000m, card.GetProperty("priceBaseCurrency").GetDecimal());

        var id = card.GetProperty("id").GetString();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles/{id}");

        Assert.Equal(2, detail.GetProperty("imageUrls").GetArrayLength());
        Assert.Equal(2, detail.GetProperty("listings").GetArrayLength());
        Assert.Equal("Vin", detail.GetProperty("canonicalHashSource").GetString());

        // Both prices are present and converted, which is what lets a person compare offers.
        var basePrices = detail.GetProperty("listings").EnumerateArray()
            .Select(l => l.GetProperty("priceBaseCurrency").GetDecimal()).OrderBy(p => p).ToArray();

        Assert.Equal([14_000m, 15_000m], basePrices);
    }

    [Fact]
    public async Task Another_tenants_vehicle_is_not_found_rather_than_forbidden()
    {
        var code = await SourceAsync();
        var marker = $"DT{Guid.NewGuid():N}"[..10];

        // A source owned by Nihon, so its stock is private to Nihon.
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var privateCode = $"pv-{Guid.NewGuid():N}"[..14];

        await nihon.PostAsJsonAsync("/api/v1/vehicle-sources", new
        {
            code = privateCode,
            name = "Private stock",
            providerType = "DealerJson",
            isShared = false,
        });

        await nihon.PostAsync($"/api/v1/vehicle-sources/{privateCode}/import",
            Upload(Document(Car("p-1", marker, "Hiace", 1_200_000m, "JPY"))));

        var mine = await nihon.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        var id = mine.GetProperty("items")[0].GetProperty("id").GetString();

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");
        var response = await karachi.GetAsync($"/api/v1/vehicles/{id}");

        // 404 rather than 403: confirming the id exists would leak the shape of another
        // tenant's inventory to anyone willing to guess.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        _ = code;
    }

    [Fact]
    public async Task Detail_requires_the_read_permission()
    {
        var client = _factory.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/vehicles/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
