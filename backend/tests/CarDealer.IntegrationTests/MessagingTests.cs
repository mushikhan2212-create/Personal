using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Preparing a WhatsApp message to a customer.
/// </summary>
/// <remarks>
/// The interim provider hands back a click-to-chat link for a person to send, so what these
/// check is that the link goes to the right person and says the right thing - and, more
/// importantly, that a number the platform cannot place refuses instead of producing a link to
/// somebody else.
/// </remarks>
public sealed class MessagingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public MessagingTests(ApiFactory factory) => _factory = factory;

    private static string Marker() => "MS" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<Guid> AddCustomerAsync(
        HttpClient client, string marker, string? phone, string? country)
    {
        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = $"{marker}Imran",
            lastName = "Sheikh",
            phone,
            countryCode = country,
            // A name alone satisfies the API when there is no phone to give.
            email = phone is null ? $"{marker}@example.test" : null,
        });

        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();
    }

    /// <summary>Puts one priced car in the catalogue. Each test class gets its own database.</summary>
    private async Task<Guid> AddVehicleAsync(string marker, int withPhotos = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var source = new VehicleSource
        {
            Name = $"Messaging source {marker}",
            Code = $"msg-{Guid.NewGuid():N}"[..16],
            ProviderType = VehicleSourceProviderType.DealerJson,
            SourceType = VehicleSourceType.File,
            IsShared = true,
        };

        db.VehicleSources.Add(source);
        await db.SaveChangesAsync();

        var vehicle = new Vehicle
        {
            PublicId = Guid.NewGuid(),
            Make = $"{marker}Toyota",
            Model = "Corolla Altis",
            ModelYear = 2016,
            Mileage = 48_000,
            MileageUnit = MileageUnit.Kilometers,
            FuelType = FuelType.Petrol,
            Transmission = Transmission.Automatic,
            SteeringSide = SteeringSide.RightHandDrive,
            Status = VehicleStatus.Active,
        };

        db.Vehicles.Add(vehicle);
        db.VehicleListings.Add(new VehicleListing
        {
            Vehicle = vehicle,
            VehicleSourceId = source.Id,
            ExternalListingId = $"{marker}-{Guid.NewGuid():N}"[..24],
            Price = 5_390m,
            CurrencyCode = "USD",
            PriceBaseCurrency = 5_390m,
            BaseCurrencyCode = "USD",
            PriceType = PriceType.FreeOnBoard,
            SourceUrl = "https://example.test/listing/1",
            FirstSeenAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
            IsActive = true,
        });

        await db.SaveChangesAsync();

        for (var i = 0; i < withPhotos; i++)
        {
            db.VehicleImages.Add(new VehicleImage
            {
                VehicleId = vehicle.Id,
                ImageUrl = $"https://images.example.test/{marker}/{i}.jpg",
                SortOrder = i,
            });
        }

        if (withPhotos > 0) await db.SaveChangesAsync();

        return vehicle.PublicId;
    }

    private static async Task<JsonElement> DraftAsync(HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/messaging/whatsapp/draft", request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_draft_carries_a_link_to_the_customers_number()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(client, m, "+92 300 1234567", "PK");

        var draft = await DraftAsync(client, new { customerPublicId = customer });

        Assert.True(draft.GetProperty("canSend").GetBoolean());
        Assert.Equal("923001234567", draft.GetProperty("normalizedPhone").GetString());
        Assert.StartsWith(
            "https://wa.me/923001234567?text=",
            draft.GetProperty("handoffUrl").GetString()!,
            StringComparison.Ordinal);

        // Addressed by first name, and signed with the tenant rather than left anonymous.
        var body = draft.GetProperty("body").GetString()!;
        Assert.Contains($"Hi {m}Imran", body, StringComparison.Ordinal);
        Assert.Contains("Nihon Motors", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_screen_is_told_the_platform_does_not_send_it()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(client, m, "+92 300 1234567", "PK");

        var draft = await DraftAsync(client, new { customerPublicId = customer });

        // The capability flags are what let the button say "Open WhatsApp" now and "Send"
        // later without the screen being rewritten - and what stops it claiming today that a
        // message was sent when a human still has to press send.
        Assert.False(draft.GetProperty("canSendDirectly").GetBoolean());
        Assert.False(draft.GetProperty("canReceive").GetBoolean());
        Assert.Equal("whatsapp", draft.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task A_number_with_no_country_refuses_rather_than_guessing_one()
    {
        // The failure worth preventing: a link built on a guessed country opens a chat with a
        // real person who is not the customer.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(client, m, "0300 1234567", null);

        var draft = await DraftAsync(client, new { customerPublicId = customer });

        Assert.False(draft.GetProperty("canSend").GetBoolean());
        Assert.Null(draft.GetProperty("handoffUrl").GetString());

        // 200 with a reason, not an error: it is something a person fixes on the customer
        // record, and the screen has to be able to say what.
        Assert.Contains(
            "no country code",
            draft.GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_customer_with_no_phone_at_all_refuses_with_a_usable_reason()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(client, m, null, "PK");

        var draft = await DraftAsync(client, new { customerPublicId = customer });

        Assert.False(draft.GetProperty("canSend").GetBoolean());
        Assert.Contains(
            "no phone number",
            draft.GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_draft_about_a_car_describes_it_without_pricing_it()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(client, m, "+92 300 1234567", "PK");

        var vehicleId = await AddVehicleAsync(m);

        var draft = await DraftAsync(client, new
        {
            customerPublicId = customer,
            vehiclePublicId = vehicleId,
        });

        var body = draft.GetProperty("body").GetString()!;

        Assert.Contains($"{m}Toyota Corolla Altis", body, StringComparison.Ordinal);
        Assert.Contains("2016", body, StringComparison.Ordinal);
        Assert.Contains("48,000 km", body, StringComparison.Ordinal);
        Assert.Contains("Petrol · Automatic · RHD", body, StringComparison.Ordinal);

        // The dealer brokers other exporters' stock, so the source listing URL names their
        // supplier. A customer who follows it can buy direct - this is margin, not tidiness.
        Assert.DoesNotContain("example.test", body, StringComparison.Ordinal);

        // And no URL of any kind, so an image address cannot leak the supplier either.
        Assert.DoesNotContain("http", body, StringComparison.OrdinalIgnoreCase);

        // No price: a quote is a conversation, not an opening line.
        Assert.DoesNotContain("5,390", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Price", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FOB", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Photos_come_back_beside_the_draft_rather_than_inside_it()
    {
        // Click-to-chat carries text only, so the photo cannot be in the message. It is offered
        // for the salesperson to attach in WhatsApp instead - which also keeps the exporter's
        // image host out of what the customer sees.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(client, m, "+92 300 1234567", "PK");
        var vehicleId = await AddVehicleAsync(m, withPhotos: 3);

        var draft = await DraftAsync(client, new
        {
            customerPublicId = customer,
            vehiclePublicId = vehicleId,
        });

        var photos = draft.GetProperty("photos").EnumerateArray().ToList();

        Assert.Equal(3, photos.Count);
        Assert.Equal(0, photos[0].GetProperty("index").GetInt32());

        // Downloaded by position through our own API, so the route never takes an address from
        // the caller and cannot be pointed anywhere else.
        Assert.Equal(
            $"/api/v1/vehicles/{vehicleId}/photos/0",
            photos[0].GetProperty("downloadUrl").GetString());

        Assert.DoesNotContain("http", draft.GetProperty("body").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_photo_that_does_not_exist_is_a_404()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var vehicleId = await AddVehicleAsync(m, withPhotos: 1);

        var response = await client.GetAsync($"/api/v1/vehicles/{vehicleId}/photos/7");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_edited_message_is_the_one_that_ends_up_in_the_link()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(client, m, "+92 300 1234567", "PK");

        var draft = await DraftAsync(client, new
        {
            customerPublicId = customer,
            body = "Salaam Imran, are you still looking?",
        });

        Assert.Equal("Salaam Imran, are you still looking?", draft.GetProperty("body").GetString());

        // Percent-encoded, not form-encoded: a space has to survive as %20 rather than becoming
        // a plus sign, which WhatsApp would show literally.
        Assert.Contains(
            "Salaam%20Imran",
            draft.GetProperty("handoffUrl").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newlines_survive_into_the_link()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(client, m, "+92 300 1234567", "PK");

        var draft = await DraftAsync(client, new { customerPublicId = customer });

        // The composed message is several lines. Encoded wrongly they collapse into one, and
        // the bulleted spec list arrives as a wall of text.
        Assert.Contains("%0A", draft.GetProperty("handoffUrl").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_tenants_customer_is_a_404()
    {
        var m = Marker();
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(nihon, m, "+92 300 1234567", "PK");

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        var response = await karachi.PostAsJsonAsync(
            "/api/v1/messaging/whatsapp/draft", new { customerPublicId = customer });

        // Also closes the side door: without the tenant filter this endpoint would hand back
        // another tenant's phone number.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_only_users_cannot_compose_messages_to_customers()
    {
        var m = Marker();
        var owner = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await AddCustomerAsync(owner, m, "+92 300 1234567", "PK");

        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var response = await readOnly.PostAsJsonAsync(
            "/api/v1/messaging/whatsapp/draft", new { customerPublicId = customer });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
