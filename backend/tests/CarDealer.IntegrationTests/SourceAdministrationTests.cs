using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Who may administer vehicle sources, and who may only read what they produced.
/// </summary>
/// <remarks>
/// Registering a shared source or importing a file into one publishes cars into the global
/// catalog that every tenant reads (decision D1). That is an administrative act, so it is held
/// to Admin and Tenant Owner. Everything below is one property stated four ways: a Sales
/// Manager - the most senior role that is not an administrator - can search the catalog and
/// tune their own view of it, and cannot change what is in it.
/// </remarks>
public sealed class SourceAdministrationTests : IClassFixture<ApiFactory>
{
    private const string SalesManager = "manager@nihon-motors.test";
    private const string Admin = "multi@example.test";

    private readonly ApiFactory _factory;

    public SourceAdministrationTests(ApiFactory factory) => _factory = factory;

    private static MultipartFormDataContent Upload()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"vehicles":[]}"""));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return new MultipartFormDataContent { { content, "file", "import.json" } };
    }

    private async Task<string> SourceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var code = $"adm-{Guid.NewGuid():N}"[..14];

        db.VehicleSources.Add(new VehicleSource
        {
            Name = $"Source {code}",
            Code = code,
            ProviderType = VehicleSourceProviderType.DealerJson,
            SourceType = VehicleSourceType.File,
            IsShared = true,
        });

        await db.SaveChangesAsync();
        return code;
    }

    [Fact]
    public async Task Sales_manager_cannot_register_a_source()
    {
        var client = await _factory.AuthenticatedClientAsync(SalesManager);

        var response = await client.PostAsJsonAsync("/api/v1/vehicle-sources", new
        {
            code = $"sm-{Guid.NewGuid():N}"[..12],
            name = "Attempted source",
            providerType = "DealerJson",
            isShared = true,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sales_manager_cannot_import_vehicles()
    {
        var code = await SourceAsync();
        var client = await _factory.AuthenticatedClientAsync(SalesManager);

        // Dry run, so even a permission failure that somehow let this through would not have
        // written anything. The status is the assertion; the harmlessness is belt and braces.
        var response = await client.PostAsync(
            $"/api/v1/vehicle-sources/{code}/import?dryRun=true", Upload());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sales_manager_cannot_sync_a_source()
    {
        var code = await SourceAsync();
        var client = await _factory.AuthenticatedClientAsync(SalesManager);

        var response = await client.PostAsync($"/api/v1/vehicle-sources/{code}/sync", null);

        // 403 specifically, not the 503 an unconfigured provider returns: authorization has to
        // run before configuration is even consulted, or the refusal would leak whether a
        // provider key is set.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sales_manager_cannot_delete_a_source_and_its_data()
    {
        var code = await SourceAsync();
        var client = await _factory.AuthenticatedClientAsync(SalesManager);

        var response = await client.DeleteAsync(
            $"/api/v1/vehicle-sources/{code}?confirm={code}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        Assert.True(await db.VehicleSources.IgnoreQueryFilters().AnyAsync(s => s.Code == code));
    }

    [Fact]
    public async Task Sales_manager_can_still_search_and_manage_their_own_sources()
    {
        // The half of the rule that is easy to break by over-tightening. Losing the ability to
        // publish cars must not cost this role the ability to look at them, or to choose which
        // sources feed their own search - that needs nothing beyond vehicles.read.
        var client = await _factory.AuthenticatedClientAsync(SalesManager);

        var search = await client.GetAsync("/api/v1/vehicles?page=1&pageSize=5");
        var mySources = await client.GetAsync("/api/v1/me/sources");

        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mySources.StatusCode);
    }

    [Fact]
    public async Task Sales_manager_holds_read_but_not_sync()
    {
        // Asserted on the login payload because that is what the frontend hides its Import and
        // Delete buttons on (SearchPage checks vehicles.sync). If this ever drifts from the
        // endpoint guards, the UI offers a button that answers 403.
        var client = _factory.CreateApiClient();
        var auth = await _factory.LoginAsync(client, SalesManager);

        Assert.NotNull(auth.Permissions);
        Assert.Contains("vehicles.read", auth.Permissions!);
        Assert.DoesNotContain("vehicles.sync", auth.Permissions!);
    }

    [Fact]
    public async Task Admin_can_still_register_a_source()
    {
        // The change has to narrow the role, not break the feature.
        var client = await _factory.AuthenticatedClientAsync(Admin, "nihon-motors");

        var response = await client.PostAsJsonAsync("/api/v1/vehicle-sources", new
        {
            code = $"ad-{Guid.NewGuid():N}"[..12],
            name = "Admin registered source",
            providerType = "DealerJson",
            isShared = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
