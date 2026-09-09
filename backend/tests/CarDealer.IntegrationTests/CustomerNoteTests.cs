using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The running log of what somebody wrote down about a customer.
/// </summary>
/// <remarks>
/// The dates are the feature. A note is worth keeping because it says what was agreed and when,
/// so the tests that matter here are the ones about time and authorship - that writing a note
/// cannot back-date it, that editing one does not silently rewrite history, and that the entries
/// come back newest first. The CRUD is incidental.
///
/// <para>
/// A note is the most personal thing on a customer record after the phone number, so the
/// isolation test is not optional: another tenant must not be able to read one, write one, or
/// learn that one exists.
/// </para>
/// </remarks>
public sealed class CustomerNoteTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CustomerNoteTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid Customer)> WithCustomerAsync(
        string email = "owner@nihon-motors.test", string? firstNote = null)
    {
        var client = await _factory.AuthenticatedClientAsync(email);

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = "Note",
            lastName = "Keeper",
            phone = "+92 300 7654321",
            notes = firstNote,
        });

        created.EnsureSuccessStatusCode();

        var publicId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();

        return (client, publicId);
    }

    private static async Task<JsonElement[]> NotesOf(HttpClient client, Guid customer)
    {
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{customer}");

        return [.. body.GetProperty("notes").EnumerateArray()];
    }

    [Fact]
    public async Task A_note_is_kept_with_the_date_and_the_person_who_wrote_it()
    {
        var (client, customer) = await WithCustomerAsync();

        var added = await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/notes",
            new { body = "Called - 7,000 is his ceiling, wants it before March." });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var notes = await NotesOf(client, customer);

        Assert.Single(notes);
        Assert.Equal(
            "Called - 7,000 is his ceiling, wants it before March.",
            notes[0].GetProperty("body").GetString());

        Assert.Equal("owner@nihon-motors.test", notes[0].GetProperty("author").GetString());

        // Not edited, so nothing claims it was.
        Assert.Equal(JsonValueKind.Null, notes[0].GetProperty("editedAtUtc").ValueKind);
    }

    [Fact]
    public async Task The_newest_note_comes_first()
    {
        // A salesperson opening a customer wants the last thing said, not the first. Ordering
        // this the other way would bury today's call under a year of history.
        var (client, customer) = await WithCustomerAsync();

        foreach (var body in new[] { "First contact", "Second call", "Third call" })
        {
            await client.PostAsJsonAsync($"/api/v1/customers/{customer}/notes", new { body });
        }

        var notes = await NotesOf(client, customer);

        Assert.Equal(3, notes.Length);
        Assert.Equal("Third call", notes[0].GetProperty("body").GetString());
        Assert.Equal("First contact", notes[2].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Editing_a_note_keeps_when_it_was_written_and_says_it_changed()
    {
        // The whole value of a log is that its dates can be trusted. An edit that moved the
        // original date, or that hid the fact of the edit, would make it worth no more than the
        // single box it replaced.
        var (client, customer) = await WithCustomerAsync();

        await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/notes", new { body = "Budget 6,000" });

        var before = (await NotesOf(client, customer))[0];
        var noteId = before.GetProperty("id").GetInt64();
        var writtenAt = before.GetProperty("createdAtUtc").GetDateTime();

        var edited = await client.PutAsJsonAsync(
            $"/api/v1/customers/{customer}/notes/{noteId}", new { body = "Budget 6,500" });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = (await NotesOf(client, customer))[0];

        Assert.Equal("Budget 6,500", after.GetProperty("body").GetString());
        Assert.Equal(writtenAt, after.GetProperty("createdAtUtc").GetDateTime());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("editedAtUtc").ValueKind);
    }

    [Fact]
    public async Task A_note_supplied_when_the_customer_is_added_starts_the_log()
    {
        // "Referred by his brother, pays cash" is the first thing anyone knows, and it belongs
        // in the same list as everything learned afterwards rather than in a separate field.
        var (client, customer) = await WithCustomerAsync(
            firstNote: "Referred by his brother, pays cash.");

        var notes = await NotesOf(client, customer);

        Assert.Single(notes);
        Assert.Equal("Referred by his brother, pays cash.", notes[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task An_empty_note_is_refused()
    {
        var (client, customer) = await WithCustomerAsync();

        var blank = await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/notes", new { body = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Empty(await NotesOf(client, customer));
    }

    [Fact]
    public async Task A_note_too_long_for_the_column_is_refused_rather_than_truncated()
    {
        // Silently cutting the end off a record of what a customer agreed to is worse than
        // refusing it - the salesperson would never know which half was kept.
        var (client, customer) = await WithCustomerAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/notes", new { body = new string('x', 4_001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_deleted_note_is_gone()
    {
        var (client, customer) = await WithCustomerAsync();

        await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/notes", new { body = "Wrong customer, my mistake" });

        var noteId = (await NotesOf(client, customer))[0].GetProperty("id").GetInt64();

        var deleted = await client.DeleteAsync($"/api/v1/customers/{customer}/notes/{noteId}");

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty(await NotesOf(client, customer));
    }

    [Fact]
    public async Task Deleting_the_customer_takes_their_notes()
    {
        // Erasure has to reach everything derived from the person (O3), and a note is the most
        // personal thing here after the phone number.
        var (client, customer) = await WithCustomerAsync();

        await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/notes", new { body = "Something about a person" });

        long customerId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            customerId = await db.Customers.IgnoreQueryFilters()
                .Where(c => c.PublicId == customer).Select(c => c.Id).FirstAsync();
        }

        var deleted = await client.DeleteAsync($"/api/v1/customers/{customer}");
        deleted.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            Assert.Empty(await db.CustomerNotes
                .IgnoreQueryFilters()
                .Where(n => n.CustomerId == customerId)
                .ToListAsync());
        }
    }

    [Fact]
    public async Task Another_tenant_can_neither_read_a_note_nor_write_one()
    {
        var (owner, customer) = await WithCustomerAsync();

        await owner.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/notes",
            new { body = "Private to this dealer." });

        var stranger = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        // 404 rather than 403 throughout: a 403 would confirm the customer exists, which is
        // itself a leak about somebody else's book.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/v1/customers/{customer}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.PostAsJsonAsync(
                $"/api/v1/customers/{customer}/notes", new { body = "Should not land" }))
                .StatusCode);

        // And nothing was written by that attempt.
        var notes = await NotesOf(owner, customer);

        Assert.Single(notes);
        Assert.Equal("Private to this dealer.", notes[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_read_only_account_can_see_notes_but_not_write_them()
    {
        var (owner, customer) = await WithCustomerAsync();

        await owner.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/notes", new { body = "Visible to the team" });

        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        Assert.Single(await NotesOf(readOnly, customer));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await readOnly.PostAsJsonAsync(
                $"/api/v1/customers/{customer}/notes", new { body = "Nope" })).StatusCode);
    }
}
