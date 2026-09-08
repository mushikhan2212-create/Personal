using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Turning a customer requirement into stock that fits it.
/// </summary>
/// <remarks>
/// A requirement is a saved search, and matching runs it through the same ISearchProvider the
/// vehicle screen uses. These tests exist to prove that translation is faithful - that each
/// field actually narrows the result - because the failure mode is silent: a requirement whose
/// budget is quietly ignored returns cars the customer cannot afford, and looks like it worked.
/// </remarks>
public sealed class CustomerMatchingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CustomerMatchingTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// Seeds four cars that differ one field at a time from what the requirement asks for.
    /// </summary>
    /// <remarks>
    /// Each is excluded by exactly one criterion, so a filter that silently does nothing shows
    /// up as an extra row rather than as a plausible-looking result set.
    /// </remarks>
    private async Task<string> SeedCatalogAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var marker = "MT" + new string([.. Guid.NewGuid().ToByteArray().Take(8)
            .Select(b => (char)('a' + (b % 16)))]);

        var source = new VehicleSource
        {
            Name = $"Match source {marker}",
            Code = $"match-{Guid.NewGuid():N}"[..16],
            ProviderType = VehicleSourceProviderType.DealerJson,
            SourceType = VehicleSourceType.File,
            IsShared = true,
        };

        db.VehicleSources.Add(source);
        await db.SaveChangesAsync();

        (string Model, int Year, int Mileage, decimal Price, FuelType Fuel)[] plan =
        [
            ("Corolla", 2019, 40_000, 6_000m, FuelType.Petrol),   // wanted
            ("Corolla", 2012, 40_000, 6_000m, FuelType.Petrol),   // too old
            ("Corolla", 2019, 40_000, 20_000m, FuelType.Petrol),  // too expensive
            ("Corolla", 2019, 40_000, 6_000m, FuelType.Diesel),   // wrong fuel
        ];

        foreach (var (model, year, mileage, price, fuel) in plan)
        {
            var vehicle = new Vehicle
            {
                PublicId = Guid.NewGuid(),
                Make = $"{marker}Toyota",
                Model = model,
                ModelYear = year,
                Mileage = mileage,
                MileageUnit = MileageUnit.Kilometers,
                FuelType = fuel,
                Transmission = Transmission.Automatic,
                SteeringSide = SteeringSide.RightHandDrive,
                BodyType = "Sedan",
                Status = VehicleStatus.Active,
            };

            db.Vehicles.Add(vehicle);
            db.VehicleListings.Add(new VehicleListing
            {
                Vehicle = vehicle,
                VehicleSourceId = source.Id,
                ExternalListingId = $"{marker}-{Guid.NewGuid():N}"[..24],
                Price = price,
                CurrencyCode = "USD",

                // Set explicitly: the base price is what a budget filters on (decision D6),
                // and a null here would exclude the car from every priced requirement.
                PriceBaseCurrency = price,
                BaseCurrencyCode = "USD",
                PriceType = PriceType.FreeOnBoard,
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();
        return marker;
    }

    private async Task<(HttpClient Client, Guid Customer, long Requirement)> WithRequirementAsync(
        object requirement)
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = "Match",
            lastName = "Tester",
            phone = "+81 90 1111 2222",
        });
        created.EnsureSuccessStatusCode();

        var customer = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();

        var added = await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/requirements", requirement);
        added.EnsureSuccessStatusCode();

        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        return (client, customer, id);
    }

    [Fact]
    public async Task Every_field_of_a_requirement_narrows_the_match()
    {
        var marker = await SeedCatalogAsync();

        var (client, customer, requirement) = await WithRequirementAsync(new
        {
            name = "A sensible Corolla",
            make = $"{marker}Toyota",
            minYear = 2015,
            maxPrice = 10_000m,
            fuelType = "Petrol",
        });

        var matches = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/customers/{customer}/requirements/{requirement}/matches");

        // One of the four. The other three are each excluded by exactly one criterion, so a
        // filter that quietly did nothing would show up here as 2, 3 or 4.
        Assert.Equal(1, matches.GetProperty("totalCount").GetInt32());

        var only = matches.GetProperty("items")[0];
        Assert.Equal(2019, only.GetProperty("year").GetInt32());
    }

    [Fact]
    public async Task A_requirement_with_no_criteria_matches_the_whole_catalog()
    {
        // The control. If this returned nothing, the tests above would pass for the wrong
        // reason - an always-empty result satisfies "narrower" trivially.
        await SeedCatalogAsync();

        var (client, customer, requirement) = await WithRequirementAsync(new { name = "Anything" });

        var matches = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/customers/{customer}/requirements/{requirement}/matches");

        Assert.True(matches.GetProperty("totalCount").GetInt32() >= 4);
    }

    [Fact]
    public async Task The_response_says_which_criteria_were_applied()
    {
        var marker = await SeedCatalogAsync();

        var (client, customer, requirement) = await WithRequirementAsync(new
        {
            make = $"{marker}Toyota",
            maxPrice = 10_000m,
            destinationCountryCode = "PK",
        });

        var matches = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/customers/{customer}/requirements/{requirement}/matches");

        var applied = matches.GetProperty("matchedOn").EnumerateArray()
            .Select(a => a.GetString()!).ToList();

        Assert.Contains(applied, a => a.StartsWith("make ", StringComparison.Ordinal));
        Assert.Contains(applied, a => a.StartsWith("price to ", StringComparison.Ordinal));

        // Destination is recorded and deliberately not filtered - the eligibility rules do not
        // exist and encoding one wrongly hides stock the customer could legally buy (O10). The
        // response says so rather than letting a salesperson assume it applied.
        Assert.Contains(applied, a => a.Contains("recorded, not filtered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Matching_another_tenants_requirement_is_a_404()
    {
        await SeedCatalogAsync();

        var (_, customer, requirement) = await WithRequirementAsync(new { name = "Private" });
        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        var response = await karachi.GetAsync(
            $"/api/v1/customers/{customer}/requirements/{requirement}/matches");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
