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
/// The dealer's own message wording, and what it is allowed to say.
/// </summary>
/// <remarks>
/// The tests that matter here are not the CRUD ones. They are the two that protect money: a
/// template's price must be the dealer's retail price rather than the exporter's asking price,
/// and a template another dealer wrote must be invisible rather than merely forbidden.
/// </remarks>
public sealed class MessageTemplateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public MessageTemplateTests(ApiFactory factory) => _factory = factory;

    private static string Marker() => "TP" + new string([.. Guid.NewGuid().ToByteArray().Take(8)
        .Select(b => (char)('a' + (b % 16)))]);

    private static async Task<Guid> AddCustomerAsync(HttpClient client, string marker)
    {
        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = $"{marker}Imran",
            lastName = "Sheikh",
            phone = $"+92 300 {Guid.NewGuid().ToString("N")[..7]}",
        });

        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();
    }

    /// <summary>Puts one car in the shared catalogue with a source listing price.</summary>
    private async Task<Guid> AddVehicleAsync(string marker, decimal listingPrice)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var source = await db.VehicleSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Code == "template-test-source");

        if (source is null)
        {
            source = new VehicleSource
            {
                Name = "Template test source",
                Code = "template-test-source",
                ProviderType = VehicleSourceProviderType.DealerJson,
                SourceType = VehicleSourceType.File,
                IsShared = true,
            };

            db.VehicleSources.Add(source);
            await db.SaveChangesAsync();
        }

        var vehicle = new Vehicle
        {
            Make = $"{marker}Toyota",
            Model = "Corolla",
            ModelYear = 2016,
            Mileage = 62_620,
            ExteriorColor = "Black",
            Transmission = Transmission.ContinuouslyVariable,
            SteeringSide = SteeringSide.RightHandDrive,
            Status = VehicleStatus.Active,
        };

        db.Vehicles.Add(vehicle);
        db.VehicleListings.Add(new VehicleListing
        {
            Vehicle = vehicle,
            VehicleSourceId = source.Id,
            ExternalListingId = $"{marker}-{Guid.NewGuid():N}"[..24],
            Price = listingPrice,
            CurrencyCode = "USD",
            PriceBaseCurrency = listingPrice,
            BaseCurrencyCode = "USD",
            PriceType = PriceType.FreeOnBoard,
            SourceUrl = "https://example-exporter.test/stock/12345",
            FirstSeenAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
            IsActive = true,
        });

        await db.SaveChangesAsync();

        return vehicle.PublicId;
    }

    private static async Task<List<JsonElement>> TemplatesAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/message-templates");

        return [.. list.GetProperty("items").EnumerateArray()];
    }

    [Fact]
    public async Task A_tenant_starts_with_the_starter_templates()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var templates = await TemplatesAsync(client);

        // The feature has to be useful before anybody writes anything, or the first thing a
        // dealer meets is an empty list and a blank editor.
        Assert.NotEmpty(templates);
        Assert.Contains(templates, t => t.GetProperty("name").GetString() == "Price quote");
    }

    [Fact]
    public async Task The_price_in_a_message_is_the_dealers_own_and_never_the_exporters()
    {
        // The test this whole feature turns on. This dealer brokers other exporters' stock: the
        // listing carries what the exporter asks, and the overlay carries what the dealer sells
        // at. If a quote ever rendered the listing price, the message would hand the customer
        // the dealer's own buying price - the entire margin, quoted to the person being quoted.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(client, m);
        var vehicle = await AddVehicleAsync(m, listingPrice: 5_720m);

        var priced = await client.PutAsJsonAsync(
            $"/api/v1/vehicles/{vehicle}/pricing",
            new { tenantPrice = 7_200m, tenantCurrencyCode = "USD" });

        priced.EnsureSuccessStatusCode();

        var quote = (await TemplatesAsync(client))
            .First(t => t.GetProperty("name").GetString() == "Price quote");

        var drafted = await client.PostAsJsonAsync("/api/v1/messaging/whatsapp/draft", new
        {
            customerPublicId = customer,
            vehiclePublicId = vehicle,
            templatePublicId = quote.GetProperty("id").GetGuid(),
        });

        drafted.EnsureSuccessStatusCode();

        var body = (await drafted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("body").GetString()!;

        Assert.Contains("7,200", body);
        Assert.DoesNotContain("5,720", body);
    }

    [Fact]
    public async Task An_unpriced_car_drops_the_price_line_rather_than_inventing_one()
    {
        // A missing line gets noticed. A plausible wrong number does not.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(client, m);
        var vehicle = await AddVehicleAsync(m, listingPrice: 5_720m);

        var quote = (await TemplatesAsync(client))
            .First(t => t.GetProperty("name").GetString() == "Price quote");

        var drafted = await client.PostAsJsonAsync("/api/v1/messaging/whatsapp/draft", new
        {
            customerPublicId = customer,
            vehiclePublicId = vehicle,
            templatePublicId = quote.GetProperty("id").GetGuid(),
        });

        drafted.EnsureSuccessStatusCode();

        var body = (await drafted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("body").GetString()!;

        Assert.DoesNotContain("5,720", body);
        Assert.DoesNotContain("Price:", body);

        // The rest of the message still arrives - only the line that had nothing behind it goes.
        Assert.Contains($"{m}Imran", body);
        Assert.Contains("Corolla", body);
    }

    [Fact]
    public async Task A_rendered_message_never_shows_an_enum_name()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(client, m);
        var vehicle = await AddVehicleAsync(m, listingPrice: 5_720m);

        // Its own template rather than a starter. Another test in this class edits a starter to
        // prove that restoring does not overwrite it, and these share one database - a test that
        // reads wording another test is allowed to change passes or fails on execution order.
        var created = await client.PostAsJsonAsync("/api/v1/message-templates", new
        {
            name = $"Specs {Guid.NewGuid():N}"[..20],
            body = "{Transmission} · {Steering}",
        });

        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var drafted = await client.PostAsJsonAsync("/api/v1/messaging/whatsapp/draft", new
        {
            customerPublicId = customer,
            vehiclePublicId = vehicle,
            templatePublicId = id,
        });

        var body = (await drafted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("body").GetString()!;

        Assert.DoesNotContain("ContinuouslyVariable", body);
        Assert.DoesNotContain("RightHandDrive", body);
        Assert.Contains("CVT", body);
    }

    [Fact]
    public async Task An_edited_body_wins_over_the_template()
    {
        // The compose box is the last word, which is what keeps this assisted rather than
        // autonomous (master prompt section 18).
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var customer = await AddCustomerAsync(client, m);
        var offer = (await TemplatesAsync(client)).First();

        var drafted = await client.PostAsJsonAsync("/api/v1/messaging/whatsapp/draft", new
        {
            customerPublicId = customer,
            templatePublicId = offer.GetProperty("id").GetGuid(),
            body = "My own words entirely.",
        });

        var body = (await drafted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("body").GetString()!;

        Assert.Equal("My own words entirely.", body);
    }

    [Fact]
    public async Task A_template_with_a_placeholder_that_does_not_exist_is_refused()
    {
        // It would otherwise render literally, braces and all, into a message somebody sends -
        // and the editor shows the author the template rather than the result, so this is the
        // last point at which anybody could notice.
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsJsonAsync("/api/v1/message-templates", new
        {
            name = $"Typo {Guid.NewGuid():N}"[..20],
            body = "Hi {FirstName}, the {Modle} has arrived.",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Named, so the author can find it. "Invalid template" would send them hunting.
        Assert.Contains("Modle", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_template_carrying_the_listing_link_is_flagged_as_revealing_the_source()
    {
        // Allowed, because the operator asked for it per-template. Flagged every time, because
        // the URL names the exporter and a customer who follows it can buy direct.
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var name = $"Link {Guid.NewGuid():N}"[..20];

        var created = await client.PostAsJsonAsync("/api/v1/message-templates", new
        {
            name,
            body = "Hi {FirstName|there},\n\nHave a look: {ListingUrl}",
        });

        created.EnsureSuccessStatusCode();

        var mine = (await TemplatesAsync(client)).First(t => t.GetProperty("name").GetString() == name);

        Assert.True(mine.GetProperty("revealsSource").GetBoolean());
        Assert.True(mine.GetProperty("needsVehicle").GetBoolean());
    }

    [Fact]
    public async Task One_tenants_templates_are_invisible_to_another()
    {
        var name = $"Private {Guid.NewGuid():N}"[..20];
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var created = await nihon.PostAsJsonAsync("/api/v1/message-templates", new
        {
            name,
            body = "Hi {FirstName|there}, this wording is ours.",
        });

        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        Assert.DoesNotContain(
            await TemplatesAsync(karachi), t => t.GetProperty("name").GetString() == name);

        // Invisible rather than merely forbidden: a 404, because confirming the id exists would
        // itself say something about another dealer's business.
        var deleted = await karachi.DeleteAsync($"/api/v1/message-templates/{id}");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        Assert.True(
            await db.MessageTemplates.IgnoreQueryFilters().AnyAsync(t => t.PublicId == id),
            "Another tenant's delete removed the template.");
    }

    [Fact]
    public async Task A_deleted_template_stays_deleted_across_a_restart()
    {
        // The lesson the vehicle sources learned the hard way. A list somebody curates is
        // theirs, and re-imposing the platform's own on every restart overrules them.
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var doomed = (await TemplatesAsync(client))
            .First(t => t.GetProperty("name").GetString() == "Follow-up");

        var deleted = await client.DeleteAsync(
            $"/api/v1/message-templates/{doomed.GetProperty("id").GetGuid()}");

        deleted.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync(includeDevelopmentUsers: true);
        }

        Assert.DoesNotContain(
            await TemplatesAsync(client), t => t.GetProperty("name").GetString() == "Follow-up");

        // And the explicit recovery path puts it back, which is why no automatic re-seed is
        // needed to protect somebody from deleting one by accident.
        var restored = await client.PostAsync("/api/v1/message-templates/restore-starters", null);
        restored.EnsureSuccessStatusCode();

        Assert.Contains(
            await TemplatesAsync(client), t => t.GetProperty("name").GetString() == "Follow-up");
    }

    [Fact]
    public async Task Restoring_starters_does_not_overwrite_wording_somebody_edited()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var offer = (await TemplatesAsync(client))
            .First(t => t.GetProperty("name").GetString() == "New car offer");

        var edited = await client.PutAsJsonAsync(
            $"/api/v1/message-templates/{offer.GetProperty("id").GetGuid()}",
            new { name = "New car offer", body = "Salaam {FirstName|ji}, dekho: {Vehicle|gaari}" });

        edited.EnsureSuccessStatusCode();

        var restored = await client.PostAsync("/api/v1/message-templates/restore-starters", null);
        restored.EnsureSuccessStatusCode();

        var after = (await TemplatesAsync(client))
            .First(t => t.GetProperty("name").GetString() == "New car offer");

        Assert.StartsWith("Salaam", after.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_read_only_user_can_neither_write_templates_nor_price_a_car()
    {
        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var created = await readOnly.PostAsJsonAsync("/api/v1/message-templates", new
        {
            name = "Nope",
            body = "Hi {FirstName|there},",
        });

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);

        var vehicle = await AddVehicleAsync(Marker(), listingPrice: 5_720m);

        // A price is the one number a customer is quoted, so it is not a read.
        var priced = await readOnly.PutAsJsonAsync(
            $"/api/v1/vehicles/{vehicle}/pricing", new { tenantPrice = 1m });

        Assert.Equal(HttpStatusCode.Forbidden, priced.StatusCode);
    }

    [Fact]
    public async Task A_price_set_by_one_tenant_is_invisible_to_another()
    {
        // The overlay is what lets a shared catalogue carry a private margin (decision D1).
        var m = Marker();
        var vehicle = await AddVehicleAsync(m, listingPrice: 5_720m);

        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var priced = await nihon.PutAsJsonAsync(
            $"/api/v1/vehicles/{vehicle}/pricing",
            new { tenantPrice = 7_200m, tenantCurrencyCode = "USD" });

        priced.EnsureSuccessStatusCode();

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");
        var seen = await karachi.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles/{vehicle}");

        Assert.Equal(JsonValueKind.Null, seen.GetProperty("tenantPrice").ValueKind);
    }

    [Fact]
    public async Task Clearing_a_price_is_not_the_same_as_setting_it_to_zero()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var vehicle = await AddVehicleAsync(m, listingPrice: 5_720m);

        await client.PutAsJsonAsync(
            $"/api/v1/vehicles/{vehicle}/pricing", new { tenantPrice = 7_200m });

        var cleared = await client.PutAsJsonAsync(
            $"/api/v1/vehicles/{vehicle}/pricing", new { tenantPrice = (decimal?)null });

        cleared.EnsureSuccessStatusCode();

        var seen = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles/{vehicle}");

        // "I have not priced this" renders no price line at all; zero would render "USD 0".
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("tenantPrice").ValueKind);
    }

    [Fact]
    public async Task A_price_defaults_to_the_tenants_own_currency()
    {
        // So a dealer quoting in one currency does not restate it on every car.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var vehicle = await AddVehicleAsync(m, listingPrice: 5_720m);

        var priced = await client.PutAsJsonAsync(
            $"/api/v1/vehicles/{vehicle}/pricing", new { tenantPrice = 7_200m });

        priced.EnsureSuccessStatusCode();

        var result = await priced.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(
            result.GetProperty("tenantCurrencyCode").GetString()));
    }

    [Fact]
    public async Task Two_templates_cannot_share_a_name()
    {
        // So the picker never shows two "Price quote" entries and nobody has to guess which one
        // they last edited.
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var name = $"Twin {Guid.NewGuid():N}"[..20];

        var first = await client.PostAsJsonAsync("/api/v1/message-templates", new
        {
            name,
            body = "Hi {FirstName|there},",
        });

        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/api/v1/message-templates", new
        {
            name,
            body = "Hi again {FirstName|there},",
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
}
