using CarDealer.Application.Abstractions;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Infrastructure.Sync;
using CarDealer.Integrations.Carapis;
using CarDealer.Integrations.FileImport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The sync run: quota, deduplication, and the counters master prompt section 8 requires.
/// </summary>
/// <remarks>
/// Runs against real SQL Server, because the behaviour under test depends on constraints the
/// in-memory provider does not enforce - the unique index over scope, source and external id
/// is what makes re-ingestion an update rather than a duplicate.
/// </remarks>
public sealed class VehicleSyncServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public VehicleSyncServiceTests(ApiFactory factory) => _factory = factory;

    /// <summary>Serves canned pages, and counts what was asked of it.</summary>
    private sealed class FakeProvider : IVehicleSourceSyncProvider
    {
        private readonly List<List<string>> _pages;

        public FakeProvider(params List<string>[] pages) => _pages = [.. pages];

        public int PagesServed { get; private set; }

        public string SourceCode => "fake";

        public VehicleSourceProviderType ProviderType => VehicleSourceProviderType.Carapis;

        public Task<VehicleSourcePage> FetchPageAsync(VehicleSourceQuery query, CancellationToken ct = default)
        {
            PagesServed++;

            var index = query.Page - 1;
            var payloads = index < _pages.Count ? _pages[index] : [];

            return Task.FromResult(new VehicleSourcePage
            {
                Records = payloads.Select((p, i) => new RawVehicleRecord
                {
                    ExternalId = $"page{query.Page}-item{i}",
                    SourceCode = query.SourceCode,
                    RawPayload = p,
                    RetrievedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
                }).ToList(),
                TotalCount = _pages.Sum(p => p.Count),
                Page = query.Page,
                TotalPages = _pages.Count,
                HasNextPage = query.Page < _pages.Count,
            });
        }
    }

    private static string Vehicle(string id, string? vin = null, string? listingId = null, string price = "4290.00")
        => $$"""
            {"id":"{{id}}","source_code":"fake","brand_name":"Toyota","model_name":"Vitz",
             "year":2017,"price_original":"{{price}}","price_original_currency":"USD",
             "vin":"{{vin ?? ""}}","listing_id":"{{listingId ?? ""}}","is_available":true}
            """;

    private async Task<(VehicleSyncService Service, FakeProvider Provider, IServiceScope Scope, long SourceId)>
        BuildAsync(params List<string>[] pages)
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var code = $"fake-{Guid.NewGuid():N}"[..16];

        var source = new VehicleSource
        {
            TenantId = null,
            Name = "Fake Source",
            Code = code,
            ProviderType = VehicleSourceProviderType.Carapis,
            SourceType = VehicleSourceType.Api,
            IsShared = true,
        };

        db.VehicleSources.Add(source);
        await db.SaveChangesAsync();

        var provider = new FakeProvider(pages);

        var service = new VehicleSyncService(
            db,
            [new CarapisNormalizer(), new ImportNormalizer()],
            scope.ServiceProvider.GetRequiredService<IDateTimeProvider>(),
            scope.ServiceProvider.GetRequiredService<IExchangeRateService>(),
            NullLogger<VehicleSyncService>.Instance,
            provider);

        return (service, provider, scope, source.Id);
    }

    private async Task<string> SourceCodeAsync(IServiceScope scope, long sourceId)
    {
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        return (await db.VehicleSources.IgnoreQueryFilters().FirstAsync(s => s.Id == sourceId)).Code;
    }

    [Fact]
    public async Task The_page_quota_is_a_hard_ceiling()
    {
        // Five pages available, three permitted. Master prompt section 18 forbids unlimited
        // synchronization, and the quota is what makes "controlled sample" true.
        var (service, provider, scope, sourceId) = await BuildAsync(
            [Vehicle("a")], [Vehicle("b")], [Vehicle("c")], [Vehicle("d")], [Vehicle("e")]);

        using (scope)
        {
            var result = await service.RunAsync(new VehicleSyncOptions
            {
                SourceCode = await SourceCodeAsync(scope, sourceId),
                MaxPages = 3,
            });

            Assert.Equal(3, provider.PagesServed);
            Assert.Equal(3, result.PagesFetched);
            Assert.Equal(3, result.Created);
        }
    }

    [Fact]
    public async Task Paging_stops_early_when_the_source_says_there_is_no_more()
    {
        var (service, provider, scope, sourceId) = await BuildAsync([Vehicle("a")], [Vehicle("b")]);

        using (scope)
        {
            await service.RunAsync(new VehicleSyncOptions
            {
                SourceCode = await SourceCodeAsync(scope, sourceId),
                MaxPages = 10,
            });

            Assert.Equal(2, provider.PagesServed);
        }
    }

    [Fact]
    public async Task Re_ingesting_the_same_listing_updates_rather_than_duplicating()
    {
        var (service, _, scope, sourceId) = await BuildAsync([Vehicle("a", listingId: "AO4106")]);

        using (scope)
        {
            var code = await SourceCodeAsync(scope, sourceId);
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var first = await service.RunAsync(new VehicleSyncOptions { SourceCode = code, MaxPages = 1 });
            var second = await service.RunAsync(new VehicleSyncOptions { SourceCode = code, MaxPages = 1 });

            Assert.Equal(1, first.Created);
            Assert.Equal(0, second.Created);
            Assert.Equal(1, second.Updated);

            var listings = await db.VehicleListings.IgnoreQueryFilters()
                .Where(l => l.VehicleSourceId == sourceId).CountAsync();

            Assert.Equal(1, listings);
        }
    }

    /// <summary>
    /// The behaviour the empty-string VIN guard protects.
    /// </summary>
    [Fact]
    public async Task Vehicles_without_a_strong_identifier_stay_distinct()
    {
        // Three vehicles, no VIN and no lot number between them - exactly what SBT Japan
        // returns on the list path. If a blank identifier produced a hash, all three would
        // merge into one row.
        var (service, _, scope, sourceId) = await BuildAsync(
            [Vehicle("a"), Vehicle("b"), Vehicle("c")]);

        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var result = await service.RunAsync(new VehicleSyncOptions
            {
                SourceCode = await SourceCodeAsync(scope, sourceId),
                MaxPages = 1,
            });

            Assert.Equal(3, result.Created);
            Assert.Equal(0, result.AutoMerged);
            Assert.Equal(3, result.WithoutStrongIdentifier);

            var vehicles = await db.Vehicles.IgnoreQueryFilters()
                .Where(v => v.Listings.Any(l => l.VehicleSourceId == sourceId)).CountAsync();

            Assert.Equal(3, vehicles);

            // And every one carries a null hash rather than a shared one.
            var hashes = await db.Vehicles.IgnoreQueryFilters()
                .Where(v => v.Listings.Any(l => l.VehicleSourceId == sourceId))
                .Select(v => v.CanonicalHash).ToListAsync();

            Assert.All(hashes, h => Assert.Null(h));
        }
    }

    [Fact]
    public async Task Two_listings_sharing_a_vin_attach_to_one_vehicle()
    {
        // The merge D3 does permit: an exact strong identifier, and nothing weaker.
        var (service, _, scope, sourceId) = await BuildAsync(
        [
            Vehicle("a", vin: "JTNBA1HK9R3039064", listingId: "L1"),
            Vehicle("b", vin: "JTNBA1HK9R3039064", listingId: "L2"),
        ]);

        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var result = await service.RunAsync(new VehicleSyncOptions
            {
                SourceCode = await SourceCodeAsync(scope, sourceId),
                MaxPages = 1,
            });

            Assert.Equal(1, result.Created);
            Assert.Equal(1, result.AutoMerged);

            var vehicles = await db.Vehicles.IgnoreQueryFilters()
                .Where(v => v.Listings.Any(l => l.VehicleSourceId == sourceId)).CountAsync();

            var listings = await db.VehicleListings.IgnoreQueryFilters()
                .Where(l => l.VehicleSourceId == sourceId).CountAsync();

            Assert.Equal(1, vehicles);   // one physical car
            Assert.Equal(2, listings);   // two offers of it
        }
    }

    [Fact]
    public async Task An_automatic_merge_is_recorded_and_reversible()
    {
        var (service, _, scope, sourceId) = await BuildAsync(
        [
            Vehicle("a", vin: "WVWZZZ1JZXW000001", listingId: "L1"),
            Vehicle("b", vin: "WVWZZZ1JZXW000001", listingId: "L2"),
        ]);

        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            await service.RunAsync(new VehicleSyncOptions
            {
                SourceCode = await SourceCodeAsync(scope, sourceId),
                MaxPages = 1,
            });

            var history = await db.VehicleMergeHistories
                .Where(h => h.ReasonsJson!.Contains("WVWZZZ1JZXW000001")).FirstOrDefaultAsync();

            Assert.NotNull(history);

            // Null MergedByUserId is what marks an automatic strong-identifier merge, as
            // against one a reviewer confirmed.
            Assert.Null(history!.MergedByUserId);
            Assert.Contains("Vin", history.ReasonsJson);
        }
    }

    [Fact]
    public async Task Everything_written_lands_in_the_global_catalog()
    {
        var (service, _, scope, sourceId) = await BuildAsync([Vehicle("a", listingId: "L1")]);

        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            await service.RunAsync(new VehicleSyncOptions
            {
                SourceCode = await SourceCodeAsync(scope, sourceId),
                MaxPages = 1,
            });

            // Null TenantId is decision D1's shared catalog. This is only permitted because the
            // sync runs with no tenant resolved; the write guard refuses it from anywhere else.
            var vehicle = await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Listings.Any(l => l.VehicleSourceId == sourceId));

            Assert.Null(vehicle.TenantId);
        }
    }

    [Fact]
    public async Task A_run_records_the_counters_the_poc_report_needs()
    {
        var (service, _, scope, sourceId) = await BuildAsync([Vehicle("a"), Vehicle("b")]);

        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var result = await service.RunAsync(new VehicleSyncOptions
            {
                SourceCode = await SourceCodeAsync(scope, sourceId),
                MaxPages = 1,
            });

            // Master prompt section 8: sync logs show counts, duration, errors and status.
            var job = await db.SyncJobs.IgnoreQueryFilters().FirstAsync(j => j.Id == result.SyncJobId);

            Assert.Equal(SyncJobStatus.Succeeded, job.Status);
            Assert.Equal(2, job.CreatedRecords);
            Assert.Equal(0, job.FailedRecords);
            Assert.NotNull(job.StartedAtUtc);
            Assert.NotNull(job.CompletedAtUtc);

            var items = await db.SyncJobItems.Where(i => i.SyncJobId == result.SyncJobId).CountAsync();
            Assert.Equal(2, items);
        }
    }

    [Fact]
    public async Task An_unparseable_record_is_skipped_without_abandoning_the_run()
    {
        var (service, _, scope, sourceId) = await BuildAsync(
            [Vehicle("a"), """{"source_code":"fake"}""", Vehicle("c")]);

        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var result = await service.RunAsync(new VehicleSyncOptions
            {
                SourceCode = await SourceCodeAsync(scope, sourceId),
                MaxPages = 1,
            });

            Assert.Equal(2, result.Created);
            Assert.Equal(1, result.Failed);
            Assert.Equal(SyncJobStatus.PartiallySucceeded, result.Status);

            var skipped = await db.SyncJobItems
                .FirstAsync(i => i.SyncJobId == result.SyncJobId && i.Status == SyncJobItemStatus.Skipped);

            Assert.NotNull(skipped.ErrorMessage);
        }
    }
}
