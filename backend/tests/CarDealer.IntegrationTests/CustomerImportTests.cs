using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Importing a dealer's contact list from a spreadsheet export.
/// </summary>
/// <remarks>
/// The behaviour worth guarding is not that a well-formed file imports - it is what happens to
/// the file that is nearly right, which is every real one. A row that cannot be read must not
/// take the other three hundred with it, a person already on the books must not be silently
/// overwritten, and none of it may see another tenant's customers.
/// </remarks>
public sealed class CustomerImportTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CustomerImportTests(ApiFactory factory) => _factory = factory;

    private static MultipartFormDataContent File(string csv)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        return new MultipartFormDataContent { { content, "file", "customers.csv" } };
    }

    private static async Task<JsonElement> ImportAsync(
        HttpClient client, string csv, bool dryRun = false)
    {
        var response = await client.PostAsync(
            $"/api/v1/customers/import?dryRun={dryRun}", File(csv));

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A marker keeps one test's rows out of another's counts on a shared database.</summary>
    private static string Marker() => "IM" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task A_well_formed_file_creates_customers()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var result = await ImportAsync(client, $"""
            First Name,Last Name,Phone,Email,City,Country,Status,Source
            {m}Ali,Raza,+92 300 1110001,{m}ali@example.test,Karachi,PK,Lead,Referral
            {m}Sara,Khan,+92 300 1110002,{m}sara@example.test,Lahore,PK,Active,WalkIn
            """);

        Assert.Equal(2, result.GetProperty("created").GetInt32());
        Assert.Equal(0, result.GetProperty("invalid").GetInt32());

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers?q={m}Ali");
        var only = found.GetProperty("items")[0];

        Assert.Equal("Karachi", only.GetProperty("city").GetString());
        Assert.Equal("PK", only.GetProperty("countryCode").GetString());
        Assert.Equal("Lead", only.GetProperty("status").GetString());
        Assert.Equal("Referral", only.GetProperty("leadSource").GetString());
    }

    [Fact]
    public async Task Headings_are_matched_however_they_are_spelled()
    {
        // The point of the alias table: a dealer should not have to rename their columns before
        // the software will read their file.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var result = await ImportAsync(client, $"""
            FIRST_NAME,surname,Mobile Number,E-Mail Address,Town,remarks
            {m}Yusuf,Ahmed,+92 300 2220001,{m}yusuf@example.test,Multan,Wants a Hiace
            """);

        Assert.Equal(1, result.GetProperty("created").GetInt32());

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers?q={m}Yusuf");
        Assert.Equal("Multan", found.GetProperty("items")[0].GetProperty("city").GetString());
    }

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var result = await ImportAsync(client, $"""
            First Name,Phone
            {m}Ghost,+92 300 3330001
            """, dryRun: true);

        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, result.GetProperty("created").GetInt32());

        // The count says one would be created. Nothing was.
        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers?q={m}Ghost");
        Assert.Equal(0, found.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_bad_row_is_reported_and_the_rest_still_import()
    {
        // The property that makes the feature usable at all. One unreadable row out of four
        // hundred must not cost the other three hundred and ninety-nine.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var result = await ImportAsync(client, $"""
            First Name,Phone,Email,Country
            {m}Good,+92 300 4440001,{m}good@example.test,PK
            {m}BadEmail,+92 300 4440002,not-an-email,PK
            ,,,
            {m}BadCountry,+92 300 4440004,{m}bc@example.test,Pakistan
            {m}AlsoGood,+92 300 4440005,{m}also@example.test,KE
            """);

        Assert.Equal(2, result.GetProperty("created").GetInt32());
        Assert.Equal(3, result.GetProperty("invalid").GetInt32());

        var problems = result.GetProperty("problems").EnumerateArray().ToList();

        // Each problem points at a row and says what is wrong with it in words the person
        // holding the spreadsheet can act on.
        Assert.Contains(problems, p => p.GetProperty("message").GetString()!
            .Contains("not an email address", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.GetProperty("message").GetString()!
            .Contains("two-letter code", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.GetProperty("message").GetString()!
            .Contains("No name and no way to contact", StringComparison.Ordinal));

        // Rows are numbered as customers, which is what the operator is looking at.
        Assert.Contains(problems, p => p.GetProperty("row").GetInt32() == 2);
    }

    [Fact]
    public async Task Someone_already_on_the_books_is_skipped_not_overwritten()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = $"{m}Existing",
            phone = "+92 300 5550001",
            notes = "Hand-written note that must survive an import.",
        });
        created.EnsureSuccessStatusCode();

        var publicId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();

        // Same person, differently punctuated, with a name that would overwrite the note.
        var result = await ImportAsync(client, $"""
            First Name,Phone,Notes
            {m}Replacement,+92-300-5550001,Note from the spreadsheet
            """);

        Assert.Equal(0, result.GetProperty("created").GetInt32());
        Assert.Equal(1, result.GetProperty("duplicates").GetInt32());

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{publicId}");

        Assert.Equal($"{m}Existing", after.GetProperty("firstName").GetString());
        Assert.Equal(
            "Hand-written note that must survive an import.",
            after.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task The_same_person_twice_in_one_file_is_imported_once()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var result = await ImportAsync(client, $"""
            First Name,Phone,Email
            {m}Twice,+92 300 6660001,{m}twice@example.test
            {m}Twice Again,+92 300 6660001,{m}other@example.test
            {m}Third,+92 300 6660003,{m}TWICE@example.test
            """);

        // The second row repeats the phone, the third repeats the email in a different case.
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(2, result.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task Quoted_commas_and_line_breaks_survive_the_round_trip()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var result = await ImportAsync(client, "First Name,Phone,City,Notes\n"
            + $"{m}Quoted,+92 300 7770001,\"Karachi, Sindh\",\"line one\nline two\"");

        Assert.Equal(1, result.GetProperty("created").GetInt32());

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers?q={m}Quoted");
        var publicId = found.GetProperty("items")[0].GetProperty("publicId").GetGuid();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{publicId}");

        Assert.Equal("Karachi, Sindh", detail.GetProperty("city").GetString());
        Assert.Equal("line one\nline two", detail.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task A_single_name_column_is_split_into_first_and_last()
    {
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        await ImportAsync(client, $"""
            Name,Phone
            {m}Imran Sheikh,+92 300 8880001
            """);

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers?q={m}Imran");
        var only = found.GetProperty("items")[0];

        Assert.Equal($"{m}Imran", only.GetProperty("firstName").GetString());
        Assert.Equal("Sheikh", only.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task A_file_with_no_recognisable_header_is_rejected_whole()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsync(
            "/api/v1/customers/import", File("alpha,beta,gamma\n1,2,3"));

        // Distinct from a file whose rows have problems, which is a 200 listing them. Here
        // nothing could be read at all, and importing it would create three empty customers.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            "header",
            problem.GetProperty("detail").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Imported_customers_belong_to_the_importing_tenant_only()
    {
        var m = Marker();
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        await ImportAsync(nihon, $"""
            First Name,Phone
            {m}Private,+92 300 9990001
            """);

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");
        var theirs = await karachi.GetFromJsonAsync<JsonElement>($"/api/v1/customers?q={m}Private");

        Assert.Equal(0, theirs.GetProperty("totalCount").GetInt32());

        // And the duplicate check does not reach across either: the same number imports
        // cleanly for the other tenant rather than being reported as already present.
        var result = await ImportAsync(karachi, $"""
            First Name,Phone
            {m}Private,+92 300 9990001
            """);

        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(0, result.GetProperty("duplicates").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var tenants = await db.Customers
            .IgnoreQueryFilters()
            .Where(c => c.FirstName == $"{m}Private")
            .Select(c => c.TenantId)
            .ToListAsync();

        // One row per tenant, each owned by the tenant that imported it.
        Assert.Equal(2, tenants.Count);
        Assert.Equal(2, tenants.Distinct().Count());
    }

    [Fact]
    public async Task Importing_needs_permission_to_manage_customers()
    {
        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var response = await readOnly.PostAsync(
            "/api/v1/customers/import", File("First Name,Phone\nNope,+92 300 0000001"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_upload_is_a_400_rather_than_a_silent_success()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsync("/api/v1/customers/import", File(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
