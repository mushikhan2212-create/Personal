using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Customers: who can see them, who can change them, and what deleting one means.
/// </summary>
/// <remarks>
/// The isolation tests here matter more than the CRUD ones. A shared vehicle catalog is the
/// product (decision D1); a shared customer list is a breach, and this is the first personal
/// data the platform holds. So the properties worth pinning are that one tenant cannot reach
/// another's customers by any route, and that deletion actually deletes.
/// </remarks>
public sealed class CustomerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CustomerTests(ApiFactory factory) => _factory = factory;

    private static object Person(string name) => new
    {
        firstName = name,
        lastName = "Tester",
        phone = "+81 90 0000 0000",
        email = $"{name.ToLowerInvariant()}@example.test",
        countryCode = "jp",
        status = "Lead",
        leadSource = "Referral",
    };

    private static async Task<Guid> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers", Person(name));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("publicId").GetGuid();
    }

    [Fact]
    public async Task A_customer_is_invisible_to_another_tenant()
    {
        // The property that makes it safe to hold this data at all.
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        var id = await CreateAsync(nihon, $"Hidden{Guid.NewGuid():N}"[..12]);

        // Not 403: the other tenant is not being told "exists but forbidden", which would
        // itself confirm the customer exists.
        Assert.Equal(HttpStatusCode.NotFound, (await karachi.GetAsync($"/api/v1/customers/{id}")).StatusCode);

        var theirList = await karachi.GetFromJsonAsync<JsonElement>("/api/v1/customers");
        var theirIds = theirList.GetProperty("items").EnumerateArray()
            .Select(c => c.GetProperty("publicId").GetGuid());

        Assert.DoesNotContain(id, theirIds);
    }

    [Fact]
    public async Task The_same_user_in_two_tenants_sees_two_separate_books()
    {
        // multi@example.test belongs to both. A user is a global identity under D2, but a
        // customer belongs to the dealership - so the token decides which book they hold.
        var inNihon = await _factory.AuthenticatedClientAsync("multi@example.test", "nihon-motors");
        var inKarachi = await _factory.AuthenticatedClientAsync("multi@example.test", "karachi-auto");

        var id = await CreateAsync(inNihon, $"Split{Guid.NewGuid():N}"[..12]);

        Assert.Equal(HttpStatusCode.OK, (await inNihon.GetAsync($"/api/v1/customers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await inKarachi.GetAsync($"/api/v1/customers/{id}")).StatusCode);
    }

    [Fact]
    public async Task Every_customer_row_carries_the_creating_tenant()
    {
        // Checked against the table rather than the API, because the API answer would be the
        // same whether the row is scoped correctly or the filter is merely hiding a mistake.
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var id = await CreateAsync(nihon, $"Scoped{Guid.NewGuid():N}"[..12]);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var row = await db.Customers
            .IgnoreQueryFilters()
            .SingleAsync(c => c.PublicId == id);

        var nihonId = await db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Slug == "nihon-motors").Select(t => t.Id).SingleAsync();

        Assert.Equal(nihonId, row.TenantId);
    }

    [Fact]
    public async Task Reading_the_book_is_not_enough_to_change_it()
    {
        var reader = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");
        var owner = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var id = await CreateAsync(owner, $"ReadOnly{Guid.NewGuid():N}"[..12]);

        // ReadOnly holds customers.read - a book someone can be shown without rewriting.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/customers/{id}")).StatusCode);

        var created = await reader.PostAsJsonAsync("/api/v1/customers", Person("Refused"));
        var deleted = await reader.DeleteAsync($"/api/v1/customers/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
    }

    [Fact]
    public async Task A_salesperson_can_record_who_they_are_selling_to()
    {
        // The other half of that rule. Selling is the job; a salesperson who cannot write down
        // a customer has no product here.
        var sales = await _factory.AuthenticatedClientAsync("sales@nihon-motors.test");

        var id = await CreateAsync(sales, $"Sold{Guid.NewGuid():N}"[..12]);

        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync($"/api/v1/customers/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_customer_with_no_name_and_no_contact_is_refused()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsJsonAsync("/api/v1/customers", new { city = "Osaka" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_customer_deletes_their_requirements()
    {
        // Erasure has to mean erasure - there is no soft-delete flag, deliberately, because
        // O3 leaves retention policy open and a flag would make "we deleted it" untrue.
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var id = await CreateAsync(client, $"Erase{Guid.NewGuid():N}"[..12]);

        var added = await client.PostAsJsonAsync(
            $"/api/v1/customers/{id}/requirements", new { name = "Hiace for the shop", make = "Toyota" });
        added.EnsureSuccessStatusCode();

        var removed = await client.DeleteAsync($"/api/v1/customers/{id}");
        removed.EnsureSuccessStatusCode();

        var outcome = await removed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, outcome.GetProperty("requirementsDeleted").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(c => c.PublicId == id));
    }

    [Fact]
    public async Task A_customer_can_be_found_by_phone_number()
    {
        // What a salesperson has in front of them when the phone rings.
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var marker = $"5{Random.Shared.Next(1000000, 9999999)}";

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = "Phone",
            lastName = "Search",
            phone = $"+81 90 {marker}",
        });
        created.EnsureSuccessStatusCode();

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers?q={marker}");

        Assert.Equal(1, found.GetProperty("totalCount").GetInt32());
    }

    // ---------------------------------------------------------------------------------
    // Editing an existing customer
    // ---------------------------------------------------------------------------------

    private async Task<(HttpClient Client, Guid Id)> AnEditableCustomerAsync()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = "Editable",
            lastName = "Person",
            phone = "+92 300 1112223",
            email = "editable@example.test",
            city = "Karachi",
            countryCode = "PK",
            preferredLanguage = "ur",
            status = "Lead",
            leadSource = "Referral",
            notes = "First contact, before anything was corrected.",
        });

        created.EnsureSuccessStatusCode();

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();

        return (client, id);
    }

    [Fact]
    public async Task A_customer_s_details_can_be_corrected()
    {
        // The everyday case this exists for: somebody changes their number.
        var (client, id) = await AnEditableCustomerAsync();

        var updated = await client.PutAsJsonAsync($"/api/v1/customers/{id}", new
        {
            firstName = "Editable",
            lastName = "Person",
            phone = "+92 300 9998887",
            email = "corrected@example.test",
            city = "Lahore",
            countryCode = "PK",
            preferredLanguage = "ur",
            status = "Active",
            leadSource = "Referral",
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{id}");

        Assert.Equal("+92 300 9998887", after.GetProperty("phone").GetString());
        Assert.Equal("corrected@example.test", after.GetProperty("email").GetString());
        Assert.Equal("Lahore", after.GetProperty("city").GetString());
        Assert.Equal("Active", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_update_applies_every_field_including_the_ones_left_out()
    {
        // Documenting the sharp edge rather than pretending it is not there. Apply() sets each
        // field from the request, so a partial body does not leave the rest alone - it clears
        // them. Any form posting here has to send the whole record back, which is why the edit
        // drawer seeds every field including ones it shows no control for.
        var (client, id) = await AnEditableCustomerAsync();

        var updated = await client.PutAsJsonAsync($"/api/v1/customers/{id}", new
        {
            firstName = "Editable",
            phone = "+92 300 1112223",
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{id}");

        Assert.Equal(JsonValueKind.Null, after.GetProperty("city").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("preferredLanguage").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("email").ValueKind);
    }

    [Fact]
    public async Task Editing_a_customer_leaves_their_notes_alone()
    {
        // Notes are their own log now. An update that carried a notes field would either
        // duplicate an entry or overwrite the column nothing reads - neither is wanted, so the
        // field is ignored on this path.
        var (client, id) = await AnEditableCustomerAsync();

        await client.PutAsJsonAsync($"/api/v1/customers/{id}", new
        {
            firstName = "Editable",
            phone = "+92 300 1112223",
            notes = "This should go nowhere.",
        });

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{id}");
        var notes = after.GetProperty("notes").EnumerateArray().ToList();

        Assert.Single(notes);
        Assert.Equal(
            "First contact, before anything was corrected.",
            notes[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Another_tenant_cannot_edit_a_customer()
    {
        var (_, id) = await AnEditableCustomerAsync();

        var stranger = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        // 404 rather than 403: a 403 would confirm the customer exists, which is itself a leak.
        var attempt = await stranger.PutAsJsonAsync($"/api/v1/customers/{id}", new
        {
            firstName = "Hijacked",
            phone = "+92 300 0000000",
        });

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
    }

    [Fact]
    public async Task A_read_only_account_cannot_edit_a_customer()
    {
        var (_, id) = await AnEditableCustomerAsync();

        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var attempt = await readOnly.PutAsJsonAsync($"/api/v1/customers/{id}", new
        {
            firstName = "Nope",
            phone = "+92 300 0000000",
        });

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }
}
