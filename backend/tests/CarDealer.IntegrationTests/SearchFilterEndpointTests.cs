using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The structured filters on the search endpoint: make, model, body type and mileage.
/// </summary>
/// <remarks>
/// These exist because of a defect found by walking the acceptance check by hand. The filters
/// were implemented in <c>SqlServerSearchProvider</c> and used by customer requirement
/// matching, but <c>VehicleSearchRequest</c> never carried them - so the same criteria that
/// narrowed a requirement to 46 cars returned 91 when typed into the search endpoint, and the
/// two paths silently disagreed.
///
/// A provider-level test could not have caught that: the provider was correct. The binding
/// between the query string and the query object was the broken part, so these go over HTTP.
/// </remarks>
public sealed class SearchFilterEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SearchFilterEndpointTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// Seeds four cars sharing a marker make, each differing from the target by one field.
    /// </summary>
    /// <remarks>
    /// Letters-only marker, for the reason recorded in <see cref="SearchTextTests"/>: a hex
    /// marker occasionally contains a model name and matches every row.
    /// </remarks>
    private async Task<string> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var marker = "SF" + new string([.. Guid.NewGuid().ToByteArray().Take(8)
            .Select(b => (char)('a' + (b % 16)))]);

        var source = new VehicleSource
        {
            Name = $"Filter source {marker}",
            Code = $"filt-{Guid.NewGuid():N}"[..16],
            ProviderType = VehicleSourceProviderType.DealerJson,
            SourceType = VehicleSourceType.File,
            IsShared = true,
        };

        db.VehicleSources.Add(source);
        await db.SaveChangesAsync();

        (string Model, string Body, int Mileage)[] plan =
        [
            ("Corolla", "Sedan", 40_000),   // the one every filter below admits
            ("Hiace", "Van", 40_000),       // wrong model, wrong body
            ("Corolla", "Wagon", 40_000),   // wrong body only
            ("Corolla", "Sedan", 150_000),  // too many kilometres only
        ];

        foreach (var (model, body, mileage) in plan)
        {
            var vehicle = new Vehicle
            {
                PublicId = Guid.NewGuid(),
                Make = $"{marker}Toyota",
                Model = model,
                BodyType = body,
                Mileage = mileage,
                MileageUnit = MileageUnit.Kilometers,
                ModelYear = 2019,
                Status = VehicleStatus.Active,
            };

            db.Vehicles.Add(vehicle);
            db.VehicleListings.Add(new VehicleListing
            {
                Vehicle = vehicle,
                VehicleSourceId = source.Id,
                ExternalListingId = $"{marker}-{Guid.NewGuid():N}"[..24],
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();
        return marker;
    }

    private static async Task<int> CountAsync(HttpClient client, string query)
    {
        var response = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?{query}");

        return response.GetProperty("totalCount").GetInt32();
    }

    [Fact]
    public async Task Make_and_model_narrow_the_search_endpoint()
    {
        var marker = await SeedAsync();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // The make alone finds all four, which is what makes the numbers below meaningful.
        Assert.Equal(4, await CountAsync(client, $"make={marker}Toyota"));

        // Three Corollas, one Hiace.
        Assert.Equal(3, await CountAsync(client, $"make={marker}Toyota&model=Corolla"));
    }

    [Fact]
    public async Task Body_type_and_mileage_narrow_the_search_endpoint()
    {
        var marker = await SeedAsync();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // Two of the three Corollas are Sedans; the Wagon is excluded.
        Assert.Equal(2, await CountAsync(client, $"make={marker}Toyota&model=Corolla&bodyType=Sedan"));

        // Of those two, one has done 150,000km.
        Assert.Equal(
            1,
            await CountAsync(client, $"make={marker}Toyota&model=Corolla&bodyType=Sedan&maxMileage=50000"));

        // And minMileage narrows from the other end, so a range is expressible.
        Assert.Equal(
            1,
            await CountAsync(client, $"make={marker}Toyota&model=Corolla&minMileage=100000"));
    }

    /// <summary>
    /// The property the customer slice depends on: a requirement and a hand-typed search that
    /// ask for the same thing return the same cars.
    /// </summary>
    /// <remarks>
    /// This is the assertion that would have failed before the request object carried these
    /// fields, and it is the one worth keeping - the others describe the filters, this one
    /// describes the promise the Customers screen makes to a salesperson.
    /// </remarks>
    [Fact]
    public async Task A_requirement_and_the_same_search_typed_by_hand_agree()
    {
        var marker = await SeedAsync();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = "Parity",
            lastName = "Check",
            phone = "+81 90 3333 4444",
        });
        created.EnsureSuccessStatusCode();

        var customer = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();

        var added = await client.PostAsJsonAsync($"/api/v1/customers/{customer}/requirements", new
        {
            make = $"{marker}Toyota",
            model = "Corolla",
            bodyType = "Sedan",
            maxMileage = 50_000,
        });
        added.EnsureSuccessStatusCode();

        var requirement = (await added.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt64();

        var matches = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/customers/{customer}/requirements/{requirement}/matches");

        var byHand = await CountAsync(
            client, $"make={marker}Toyota&model=Corolla&bodyType=Sedan&maxMileage=50000");

        Assert.Equal(byHand, matches.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, byHand);
    }

    [Fact]
    public async Task An_inverted_mileage_range_is_rejected()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.GetAsync("/api/v1/vehicles?minMileage=90000&maxMileage=10000");

        // Rather than silently returning nothing, which reads as "no such car exists".
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
