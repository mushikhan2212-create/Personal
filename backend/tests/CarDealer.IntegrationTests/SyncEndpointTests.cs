using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The sync endpoint, driven over HTTP as an authenticated tenant user.
/// </summary>
/// <remarks>
/// This class exists because of a defect the rest of the suite structurally could not see. A
/// sync writes the GLOBAL catalog - TenantId null - and GuardGlobalCatalogWrites refuses a
/// global write from any context with a tenant resolved. VehicleSyncServiceTests drives the
/// service directly against a tenant-less context, so it never meets the guard; the endpoint
/// tests ran with no provider configured, so they returned 503 and stopped before the write.
/// Both suites were green while POST /sync answered 500 for every real caller.
///
/// The lesson is in the setup rather than the assertions: the bug lived in the seam between
/// the controller's DI scope and the service's, so only a test that goes through HTTP *with a
/// provider configured* can reach it.
///
/// Decision D12's permitted-source guard is deliberately not tested here. It lives in the real
/// Carapis provider, which this class replaces, so an assertion here would only be testing the
/// stub. CarapisVehicleProviderTests covers it against the real thing.
/// </remarks>
public sealed class SyncEndpointTests : IClassFixture<SyncApiFactory>
{
    private readonly SyncApiFactory _factory;

    public SyncEndpointTests(SyncApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Sync_from_a_tenant_authenticated_request_writes_the_global_catalog()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsync(
            "/api/v1/vehicle-sources/sbtjapan/sync?maxPages=1&pageSize=2", content: null);

        // The regression: this was a 500, because the caller's tenant followed the write into
        // the global catalog and the guard - correctly - refused it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Succeeded", body.GetProperty("status").GetString());

        // Created or updated depending on whether a sibling test ran first; both mean the
        // write reached the database, which is the whole point.
        Assert.Equal(2, body.GetProperty("created").GetInt32() + body.GetProperty("updated").GetInt32());
        Assert.Equal(0, body.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Synced_rows_are_global_and_visible_to_a_tenant_that_never_ran_the_sync()
    {
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        await nihon.PostAsync("/api/v1/vehicle-sources/sbtjapan/sync?maxPages=1&pageSize=2", null);

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");
        var seen = await karachi.GetFromJsonAsync<JsonElement>("/api/v1/vehicles");

        // A tenant that did not import the data still sees it. That is what makes the catalog
        // shared, rather than merely readable by whoever paid for the request.
        Assert.Equal(2, seen.GetProperty("totalCount").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var owners = await db.Vehicles.IgnoreQueryFilters().Select(v => v.TenantId).ToListAsync();

        Assert.NotEmpty(owners);
        Assert.All(owners, Assert.Null);
    }

    [Fact]
    public async Task A_user_without_the_sync_permission_is_refused()
    {
        var client = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var response = await client.PostAsync("/api/v1/vehicle-sources/sbtjapan/sync", null);

        // Running the sync in an unscoped context must not have loosened who may start one.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_source_code_is_a_404_rather_than_a_started_job()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsync("/api/v1/vehicle-sources/not-a-source/sync", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>An <see cref="ApiFactory"/> with a sync provider configured, so the path runs.</summary>
public sealed class SyncApiFactory : ApiFactory
{
    public StubSyncProvider Provider { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Registered after the application's own, so this replacement wins. The subject here
        // is the path from the controller to the database, so the network stays out of it.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVehicleSourceSyncProvider>();
            services.AddSingleton<IVehicleSourceSyncProvider>(Provider);
        });

        return base.CreateHost(builder);
    }
}

/// <summary>Returns canned records, and counts its calls so "no request was made" is provable.</summary>
public sealed class StubSyncProvider : IVehicleSourceSyncProvider
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public string SourceCode => "sbtjapan";

    public VehicleSourceProviderType ProviderType => VehicleSourceProviderType.Carapis;

    public Task<VehicleSourcePage> FetchPageAsync(
        VehicleSourceQuery query, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _callCount);

        // Shaped like the real payload, and stable across calls: the same two VINs must
        // upsert rather than duplicate when a second test re-runs the sync.
        var records = Enumerable.Range(1, query.PageSize).Select(i => new RawVehicleRecord
        {
            ExternalId = $"stub-{i}",
            SourceCode = query.SourceCode,
            RetrievedAtUtc = DateTime.UtcNow,
            RawPayload = $$"""
                {
                  "id": "stub-{{i}}",
                  "source_code": "{{query.SourceCode}}",
                  "brand_name": "Toyota",
                  "model_name": "Hiace",
                  "trim": "GL",
                  "year": 2018,
                  "mileage": {{i}}0000,
                  "fuel_type": "diesel",
                  "transmission": "auto",
                  "drive_type": "rwd",
                  "vin": "JTFSX23P9000{{i}}1234",
                  "price_original": "1500000",
                  "price_original_currency": "JPY",
                  "listing_url": "https://www.sbtjapan.com/used-cars/stub-{{i}}",
                  "is_available": true
                }
                """,
        }).ToList();

        return Task.FromResult(new VehicleSourcePage
        {
            Records = records,
            TotalCount = records.Count,
            Page = query.Page,
            TotalPages = 1,
            HasNextPage = false,
            Elapsed = TimeSpan.FromMilliseconds(5),
        });
    }
}
