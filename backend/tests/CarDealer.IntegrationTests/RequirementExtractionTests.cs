using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Application.AI;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Reading a customer's message, and the promise made about it.
/// </summary>
/// <remarks>
/// The tests that matter here are not the ones about extraction working. They are the ones about
/// what left the building and what was written down, because those are the product owner's O4
/// decision and the only way it stays true is if something fails when it stops being true.
/// </remarks>
public sealed class RequirementExtractionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RequirementExtractionTests(ApiFactory factory) => _factory = factory;

    private const string Enquiry = """
        Asalam o alaikum, main Imran Sheikh, Lahore se. Mera number 0300-1234567 hai aur
        email imran.sheikh@gmail.com. Corolla Axio chahiye 2017 ya newer, under 35 lakh,
        mileage 100,000 se kam. CNIC 35202-1234567-8.
        """;

    private static string Marker() => "RX" + new string([.. Guid.NewGuid().ToByteArray().Take(8)
        .Select(b => (char)('a' + (b % 16)))]);

    private (WebApplicationFactory<Program> Factory, ScriptedAIProvider Provider) WithScripted()
    {
        var provider = new ScriptedAIProvider();

        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAIProvider>();
                services.AddScoped<IAIProvider>(_ => provider);
            }));

        return (factory, provider);
    }

    private async Task<HttpClient> SignedInAsync(
        WebApplicationFactory<Program> factory, string email)
    {
        var client = factory.CreateClient();
        var auth = await _factory.LoginAsync(client, email);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return client;
    }

    private static async Task<Guid> CustomerAsync(HttpClient client, string marker)
    {
        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = "Imran",
            lastName = "Sheikh",
            phone = $"+92 300 {Guid.NewGuid().ToString("N")[..7]}",
        });

        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();
    }

    private static async Task<JsonElement> ReadAsync(
        HttpClient client, Guid customer, string message)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/requirements/read", new { message });

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>An answer quoting the message it was given, as an honest model would.</summary>
    private static AIExtractionResult Reads(params (string Field, string Value, string Evidence)[] found)
        => new()
        {
            Fields = [.. found.Select(f => new ExtractedField
            {
                Field = f.Field,
                Value = f.Value,
                Evidence = f.Evidence,
            })],
            Usage = new AIUsage { InputTokens = 300, OutputTokens = 120 },
            Provider = "scripted",
            Model = "scripted-model",
        };

    [Fact]
    public async Task No_identifier_ever_reaches_the_provider()
    {
        // The O4 promise, asserted rather than trusted. If this fails, the feature is doing the
        // one thing it was permitted on condition of not doing.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Extraction = _ => Reads((ExtractionFields.MinYear, "2017", "2017 ya newer"));

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        await ReadAsync(client, customer, Enquiry);

        var sent = provider.LastExtraction!.Text;

        Assert.DoesNotContain("0300-1234567", sent);
        Assert.DoesNotContain("imran.sheikh@gmail.com", sent);
        Assert.DoesNotContain("35202-1234567-8", sent);
        Assert.DoesNotContain("Imran", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sheikh", sent, StringComparison.OrdinalIgnoreCase);

        // And the car facts survived, or the redaction has made the feature pointless.
        Assert.Contains("Corolla Axio", sent);
        Assert.Contains("2017", sent);
        Assert.Contains("35 lakh", sent);
    }

    [Fact]
    public async Task The_message_is_never_written_to_the_audit_row()
    {
        // The other half of the decision. Redacting on the wire and then storing the original
        // would move the problem rather than solve it.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Extraction = _ => Reads((ExtractionFields.MinYear, "2017", "2017 ya newer"));

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        await ReadAsync(client, customer, Enquiry);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var audit = await db.AIRequests.IgnoreQueryFilters()
            .Where(r => r.Operation == "extract")
            .OrderByDescending(r => r.Id)
            .FirstAsync();

        Assert.Equal(AIRequestStatus.Succeeded, audit.Status);

        var stored = audit.InputMetadataJson ?? string.Empty;

        // Measurements, not content. Not the message, not the redacted message, not a fragment.
        Assert.DoesNotContain("Corolla", stored);
        Assert.DoesNotContain("Imran", stored);
        Assert.DoesNotContain("lakh", stored);
        Assert.Contains("characters", stored);
        Assert.Contains("redactedItems", stored);
    }

    [Fact]
    public async Task What_the_message_states_comes_back_with_the_words_behind_it()
    {
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Extraction = _ => Reads(
            (ExtractionFields.Model, "Corolla Axio", "Corolla Axio chahiye"),
            (ExtractionFields.MinYear, "2017", "2017 ya newer"),
            (ExtractionFields.MaxPrice, "3500000", "under 35 lakh"));

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        var result = await ReadAsync(client, customer, Enquiry);

        Assert.Null(result.GetProperty("notice").GetString());

        var fields = result.GetProperty("fields").EnumerateArray()
            .ToDictionary(
                f => f.GetProperty("field").GetString()!,
                f => (f.GetProperty("value").GetString(), f.GetProperty("evidence").GetString()));

        Assert.Equal(("3500000", "under 35 lakh"), fields[ExtractionFields.MaxPrice]);
        Assert.Equal(("2017", "2017 ya newer"), fields[ExtractionFields.MinYear]);

        // Five identifiers taken out - email, CNIC, phone, and both halves of the name,
        // which come from the customer's own record rather than from any pattern. The screen is
        // told the count so it can say so.
        Assert.Equal(5, result.GetProperty("redacted").GetInt32());
    }

    [Fact]
    public async Task A_budget_the_customer_never_named_is_thrown_away()
    {
        // The guard doing its job end to end. The operator gets nothing rather than a plausible
        // figure, and the audit records that an answer was paid for and not used.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Extraction = _ => Reads(
            (ExtractionFields.MaxPrice, "1500000", "budget 15 lakh tak"));

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        var result = await ReadAsync(client, customer, Enquiry);

        Assert.Empty(result.GetProperty("fields").EnumerateArray());
        Assert.Contains("not in the message", result.GetProperty("notice").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var audit = await db.AIRequests.IgnoreQueryFilters()
            .Where(r => r.Operation == "extract")
            .OrderByDescending(r => r.Id)
            .FirstAsync();

        Assert.Equal(AIRequestStatus.Rejected, audit.Status);

        // And the answer that was refused is kept. A rejection names the rule that broke; only
        // the response says what the model actually produced, and the first real failure in the
        // field was a value of "}, {" that nothing had recorded.
        Assert.Contains("1500000", audit.OutputMetadataJson);
    }

    [Fact]
    public async Task Nothing_is_saved_to_the_customer()
    {
        // Reads, never writes. The operator reviews and creates the requirement themselves,
        // through the endpoint that has always done that.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Extraction = _ => Reads((ExtractionFields.MinYear, "2017", "2017 ya newer"));

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        await ReadAsync(client, customer, Enquiry);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{customer}");

        Assert.Empty(after.GetProperty("requirements").EnumerateArray());
    }

    [Fact]
    public async Task With_no_provider_configured_it_says_so_and_sends_nothing()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        var result = await ReadAsync(client, customer, Enquiry);

        Assert.False(result.GetProperty("providerConfigured").GetBoolean());
        Assert.Contains("typed in", result.GetProperty("notice").GetString());
        Assert.Empty(result.GetProperty("fields").EnumerateArray());

        // Still redacted. The count is honest about work that happened before the call was
        // abandoned, and proves nothing raw was sitting in a variable waiting to be sent.
        Assert.Equal(5, result.GetProperty("redacted").GetInt32());
    }

    [Fact]
    public async Task An_oversized_paste_is_refused_before_it_costs_anything()
    {
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        var result = await ReadAsync(client, customer, new string('x', 5_000));

        Assert.Contains("limit is", result.GetProperty("notice").GetString());
        Assert.Equal(0, provider.ExtractionCalls);
    }

    [Fact]
    public async Task A_read_only_user_cannot_spend_money()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var response = await readOnly.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/requirements/read", new { message = Enquiry });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_a_message_for_someone_elses_customer()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var customer = await CustomerAsync(client, m);

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        var response = await karachi.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/requirements/read", new { message = Enquiry });

        // A 404 rather than a 403, as everywhere else: confirming the id exists would leak that
        // somebody has a customer of that name.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
