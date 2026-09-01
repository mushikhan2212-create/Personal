using CarDealer.Application.Search;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Infrastructure.Search;
using CarDealer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Search behaviour, including the parts that only exist because the catalog is shared.
/// </summary>
/// <remarks>
/// Real SQL Server rather than the in-memory provider: the isolation this asserts comes from
/// the DbContext's global query filters translated into SQL, and asserting it against a
/// provider that treats those differently would prove nothing.
/// </remarks>
public sealed class SearchProviderTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SearchProviderTests(ApiFactory factory) => _factory = factory;

    private sealed record Seeded(long GlobalVehicleId, long PrivateVehicleId, long NihonId, long KarachiId);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var tenants = await db.Tenants.OrderBy(t => t.Id).Take(2).ToListAsync();
        var (nihon, karachi) = (tenants[0].Id, tenants[1].Id);

        var marker = $"SEARCHTEST{Guid.NewGuid():N}"[..20];

        var source = new VehicleSource
        {
            TenantId = null,
            Name = "SBT Japan",
            Code = $"src-{Guid.NewGuid():N}"[..16],
            ProviderType = VehicleSourceProviderType.Carapis,
            SourceType = VehicleSourceType.Api,
            IsShared = true,
        };
        db.VehicleSources.Add(source);
        await db.SaveChangesAsync();

        async Task<long> AddAsync(long? tenantId, string model, decimal? basePrice, SteeringSide steering)
        {
            var vehicle = new Vehicle
            {
                TenantId = tenantId,
                PublicId = Guid.NewGuid(),
                Make = marker,
                Model = model,
                ModelYear = 2018,
                SteeringSide = steering,
                Status = VehicleStatus.Active,
            };

            db.Vehicles.Add(vehicle);

            db.VehicleListings.Add(new VehicleListing
            {
                TenantId = tenantId,
                Vehicle = vehicle,
                VehicleSourceId = source.Id,
                ExternalListingId = $"{model}-{Guid.NewGuid():N}"[..24],
                Price = 5000m,
                CurrencyCode = "USD",
                PriceBaseCurrency = basePrice,
                BaseCurrencyCode = basePrice is null ? null : "USD",
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                IsActive = true,
            });

            await db.SaveChangesAsync();
            return vehicle.Id;
        }

        // Seeded with no tenant resolved - the sync path. A global row and one private to Karachi.
        var global = await AddAsync(null, $"{marker}Global", 5000m, SteeringSide.RightHandDrive);
        var priv = await AddAsync(karachi, $"{marker}Private", 6000m, SteeringSide.LeftHandDrive);
        await AddAsync(null, $"{marker}NoPrice", null, SteeringSide.RightHandDrive);

        return new Seeded(global, priv, nihon, karachi);
    }

    private static (IServiceScope Scope, ISearchProvider Search, CarDealerDbContext Db) For(
        ApiFactory factory, long tenantId)
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        return (scope, new SqlServerSearchProvider(db), db);
    }

    private static async Task<VehicleSearchResult> SearchAsync(
        ISearchProvider search, string text, Action<VehicleSearchQuery>? _ = null)
        => await search.SearchAsync(new VehicleSearchQuery { Text = text, PageSize = 100 });

    [Fact]
    public async Task A_tenant_sees_global_vehicles_but_not_another_tenants_private_ones()
    {
        var seeded = await SeedAsync();
        var (scope, search, db) = For(_factory, seeded.NihonId);

        using (scope)
        {
            var marker = (await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.GlobalVehicleId)).Make!;

            var result = await SearchAsync(search, marker);
            var ids = new HashSet<Guid>(result.Hits.Select(h => h.PublicId));

            var globalPublicId = (await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.GlobalVehicleId)).PublicId;

            var privatePublicId = (await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.PrivateVehicleId)).PublicId;

            Assert.Contains(globalPublicId, ids);
            Assert.DoesNotContain(privatePublicId, ids);
        }
    }

    [Fact]
    public async Task A_tenant_can_hide_a_shared_vehicle_from_their_own_search_only()
    {
        var seeded = await SeedAsync();

        string marker;
        Guid hiddenPublicId;

        var (writeScope, _, writeDb) = For(_factory, seeded.NihonId);
        using (writeScope)
        {
            var vehicle = await writeDb.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.GlobalVehicleId);

            marker = vehicle.Make!;
            hiddenPublicId = vehicle.PublicId;

            writeDb.TenantVehicles.Add(new TenantVehicle
            {
                TenantId = seeded.NihonId,
                VehicleId = seeded.GlobalVehicleId,
                IsHidden = true,
            });

            await writeDb.SaveChangesAsync();
        }

        var (nihonScope, nihonSearch, _) = For(_factory, seeded.NihonId);
        using (nihonScope)
        {
            var hits = (await SearchAsync(nihonSearch, marker)).Hits.Select(h => h.PublicId);
            Assert.DoesNotContain(hiddenPublicId, hits);
        }

        // Hiding is per-tenant commercial state over a shared row. Karachi is unaffected.
        var (karachiScope, karachiSearch, _) = For(_factory, seeded.KarachiId);
        using (karachiScope)
        {
            var hits = (await SearchAsync(karachiSearch, marker)).Hits.Select(h => h.PublicId);
            Assert.Contains(hiddenPublicId, hits);
        }
    }

    [Fact]
    public async Task A_tenants_own_price_is_returned_and_is_invisible_to_others()
    {
        var seeded = await SeedAsync();

        string marker;
        Guid publicId;

        var (writeScope, _, writeDb) = For(_factory, seeded.NihonId);
        using (writeScope)
        {
            var vehicle = await writeDb.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.GlobalVehicleId);

            marker = vehicle.Make!;
            publicId = vehicle.PublicId;

            writeDb.TenantVehicles.Add(new TenantVehicle
            {
                TenantId = seeded.NihonId,
                VehicleId = seeded.GlobalVehicleId,
                TenantPrice = 999_999m,
                TenantCurrencyCode = "JPY",
            });

            await writeDb.SaveChangesAsync();
        }

        var (nihonScope, nihonSearch, _) = For(_factory, seeded.NihonId);
        using (nihonScope)
        {
            var hit = (await SearchAsync(nihonSearch, marker)).Hits.First(h => h.PublicId == publicId);
            Assert.Equal(999_999m, hit.TenantPrice);
            Assert.Equal("JPY", hit.TenantCurrencyCode);
        }

        var (karachiScope, karachiSearch, _) = For(_factory, seeded.KarachiId);
        using (karachiScope)
        {
            var hit = (await SearchAsync(karachiSearch, marker)).Hits.First(h => h.PublicId == publicId);
            Assert.Null(hit.TenantPrice);
        }
    }

    [Fact]
    public async Task A_listing_with_no_base_price_is_excluded_from_a_price_range_rather_than_read_as_zero()
    {
        var seeded = await SeedAsync();
        var (scope, search, db) = For(_factory, seeded.NihonId);

        using (scope)
        {
            var marker = (await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.GlobalVehicleId)).Make!;

            var unfiltered = await search.SearchAsync(new VehicleSearchQuery { Text = marker, PageSize = 100 });

            // Decision D6: a listing whose FX was unavailable at sync time has a null base
            // price. It is not a free car, so a "under 1000" filter must not return it.
            var ranged = await search.SearchAsync(new VehicleSearchQuery
            {
                Text = marker, PageSize = 100, MinPriceBase = 0m, MaxPriceBase = 1_000m,
            });

            Assert.Contains(unfiltered.Hits, h => h.PriceBaseCurrency is null);
            Assert.Empty(ranged.Hits);
        }
    }

    [Fact]
    public async Task Steering_side_filters_because_it_is_the_filter_this_trade_uses()
    {
        var seeded = await SeedAsync();
        var (scope, search, db) = For(_factory, seeded.NihonId);

        using (scope)
        {
            var marker = (await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.GlobalVehicleId)).Make!;

            var rhd = await search.SearchAsync(new VehicleSearchQuery
            {
                Text = marker, PageSize = 100, SteeringSide = SteeringSide.RightHandDrive,
            });

            Assert.NotEmpty(rhd.Hits);
            Assert.All(rhd.Hits, h => Assert.Equal(SteeringSide.RightHandDrive, h.SteeringSide));
        }
    }

    [Fact]
    public async Task Source_attribution_is_returned_with_every_hit()
    {
        var seeded = await SeedAsync();
        var (scope, search, db) = For(_factory, seeded.NihonId);

        using (scope)
        {
            var marker = (await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.GlobalVehicleId)).Make!;

            var result = await SearchAsync(search, marker);

            // Master prompt section 8 requires source attribution be visible in the UI, which
            // means search has to carry it.
            Assert.All(result.Hits, h => Assert.False(string.IsNullOrWhiteSpace(h.SourceName)));
        }
    }

    [Fact]
    public async Task Elapsed_time_is_reported_so_the_poc_can_measure_p95()
    {
        var seeded = await SeedAsync();
        var (scope, search, db) = For(_factory, seeded.NihonId);

        using (scope)
        {
            var marker = (await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.GlobalVehicleId)).Make!;

            var result = await SearchAsync(search, marker);

            // Decision D4 makes this measurement the gate for adding a search engine.
            Assert.True(result.Elapsed > TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Unresolved_tenant_context_returns_only_the_global_catalog()
    {
        var seeded = await SeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var search = new SqlServerSearchProvider(db);

        var marker = (await db.Vehicles.IgnoreQueryFilters()
            .FirstAsync(v => v.Id == seeded.GlobalVehicleId)).Make!;

        var privatePublicId = (await db.Vehicles.IgnoreQueryFilters()
            .FirstAsync(v => v.Id == seeded.PrivateVehicleId)).PublicId;

        var hits = (await SearchAsync(search, marker)).Hits.Select(h => h.PublicId).ToList();

        // No tenant resolved means TenantIdOrZero is 0, which matches no tenant - so the
        // private row stays hidden while global rows remain visible. Fail closed.
        Assert.DoesNotContain(privatePublicId, hits);
        Assert.NotEmpty(hits);
    }
}
