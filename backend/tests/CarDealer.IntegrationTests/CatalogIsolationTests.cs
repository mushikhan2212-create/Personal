using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The four global-catalog isolation cases required by schema delta section 1.4.
/// </summary>
/// <remarks>
/// Decision D1 makes Vehicles.TenantId nullable, where null means a globally shared row. The
/// query filter therefore admits "TenantId == null OR TenantId == current", which is a
/// materially weaker guard than the flat equality used everywhere else: a read filter that
/// permits null also permits UPDATE and DELETE against those same shared rows.
///
/// Case 3 is the one the filter does not give for free, and the one that matters most
/// commercially - a tenant editing the price on a shared car would change it for every other
/// tenant at once.
/// </remarks>
public sealed class CatalogIsolationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CatalogIsolationTests(ApiFactory factory) => _factory = factory;

    /// <summary>Seeds one global vehicle and one owned by each tenant. Returns their ids.</summary>
    private async Task<(long Global, long Nihon, long Karachi, long SourceId)> SeedCatalogAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var tenants = await db.Tenants.OrderBy(t => t.Id).Take(2).ToListAsync();
        var nihonId = tenants[0].Id;
        var karachiId = tenants[1].Id;

        var source = await db.VehicleSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Code == "test-shared");

        if (source is null)
        {
            source = new VehicleSource
            {
                TenantId = null,
                Name = "Test Shared Source",
                Code = "test-shared",
                ProviderType = VehicleSourceProviderType.Carapis,
                SourceType = VehicleSourceType.Api,
                IsShared = true,
                IsActive = true,
            };
            db.VehicleSources.Add(source);
            await db.SaveChangesAsync();
        }

        async Task<long> AddVehicleAsync(long? tenantId, string lot)
        {
            var existing = await db.Vehicles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(v => v.LotNumber == lot);

            if (existing is not null)
            {
                return existing.Id;
            }

            var vehicle = new Vehicle
            {
                TenantId = tenantId,
                PublicId = Guid.NewGuid(),
                Make = "Toyota",
                Model = "Hiace",
                ModelYear = 2018,
                LotNumber = lot,
                SteeringSide = SteeringSide.RightHandDrive,
                Status = VehicleStatus.Active,
            };
            db.Vehicles.Add(vehicle);
            await db.SaveChangesAsync();
            return vehicle.Id;
        }

        // Seeded with no tenant resolved, which is the sync-job path and the only route by
        // which a global row may legitimately be written.
        var global = await AddVehicleAsync(null, "LOT-GLOBAL");
        var nihon = await AddVehicleAsync(nihonId, "LOT-NIHON");
        var karachi = await AddVehicleAsync(karachiId, "LOT-KARACHI");

        return (global, nihon, karachi, source.Id);
    }

    /// <summary>Builds a DbContext whose tenant context is pinned to one tenant.</summary>
    private static (IServiceScope Scope, CarDealerDbContext Db) ScopedFor(
        ApiFactory factory, long tenantId)
    {
        var scope = factory.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantContext.SetTenant(tenantId);
        return (scope, scope.ServiceProvider.GetRequiredService<CarDealerDbContext>());
    }

    private async Task<(long Nihon, long Karachi)> TenantIdsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var tenants = await db.Tenants.OrderBy(t => t.Id).Take(2).ToListAsync();
        return (tenants[0].Id, tenants[1].Id);
    }

    /// <summary>Case 1: tenant A cannot read tenant B's private vehicles.</summary>
    [Fact]
    public async Task Tenant_cannot_read_another_tenants_private_vehicles()
    {
        var seeded = await SeedCatalogAsync();
        var (nihonId, _) = await TenantIdsAsync();

        var (scope, db) = ScopedFor(_factory, nihonId);
        using (scope)
        {
            var visible = await db.Vehicles.Select(v => v.Id).ToListAsync();

            Assert.Contains(seeded.Nihon, visible);
            Assert.DoesNotContain(seeded.Karachi, visible);
        }
    }

    /// <summary>Case 2: tenant A CAN read global vehicles. This is the point of D1.</summary>
    [Fact]
    public async Task Tenant_can_read_global_vehicles()
    {
        var seeded = await SeedCatalogAsync();
        var (nihonId, karachiId) = await TenantIdsAsync();

        foreach (var tenantId in new[] { nihonId, karachiId })
        {
            var (scope, db) = ScopedFor(_factory, tenantId);
            using (scope)
            {
                var visible = await db.Vehicles.Select(v => v.Id).ToListAsync();
                Assert.Contains(seeded.Global, visible);
            }
        }
    }

    /// <summary>
    /// Case 3: tenant A cannot UPDATE a global vehicle, even though it can read it.
    /// </summary>
    /// <remarks>
    /// The case the read filter does not cover. A tenant that could write here would change
    /// the shared catalog for every other tenant simultaneously.
    /// </remarks>
    [Fact]
    public async Task Tenant_cannot_update_a_global_vehicle()
    {
        var seeded = await SeedCatalogAsync();
        var (nihonId, _) = await TenantIdsAsync();

        var (scope, db) = ScopedFor(_factory, nihonId);
        using (scope)
        {
            var global = await db.Vehicles.FirstAsync(v => v.Id == seeded.Global);
            global.Model = "TAMPERED";

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => db.SaveChangesAsync());

            Assert.Contains("global", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // And the row is genuinely untouched.
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var row = await verifyDb.Vehicles.IgnoreQueryFilters().FirstAsync(v => v.Id == seeded.Global);
        Assert.NotEqual("TAMPERED", row.Model);
    }

    /// <summary>Case 3, delete half: reading a global row does not permit removing it.</summary>
    [Fact]
    public async Task Tenant_cannot_delete_a_global_vehicle()
    {
        var seeded = await SeedCatalogAsync();
        var (nihonId, _) = await TenantIdsAsync();

        var (scope, db) = ScopedFor(_factory, nihonId);
        using (scope)
        {
            var global = await db.Vehicles.FirstAsync(v => v.Id == seeded.Global);
            db.Vehicles.Remove(global);

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        Assert.True(await verifyDb.Vehicles.IgnoreQueryFilters().AnyAsync(v => v.Id == seeded.Global));
    }

    /// <summary>A tenant also cannot CREATE a global row from a tenant-scoped path.</summary>
    [Fact]
    public async Task Tenant_cannot_create_a_global_vehicle()
    {
        await SeedCatalogAsync();
        var (nihonId, _) = await TenantIdsAsync();

        var (scope, db) = ScopedFor(_factory, nihonId);
        using (scope)
        {
            db.Vehicles.Add(new Vehicle
            {
                TenantId = null,
                PublicId = Guid.NewGuid(),
                Make = "Nissan",
                Model = "Caravan",
                LotNumber = "LOT-SMUGGLED",
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    /// <summary>A tenant cannot write a row owned by a different tenant either.</summary>
    [Fact]
    public async Task Tenant_cannot_update_another_tenants_vehicle()
    {
        var seeded = await SeedCatalogAsync();
        var (nihonId, _) = await TenantIdsAsync();

        var (scope, db) = ScopedFor(_factory, nihonId);
        using (scope)
        {
            // Reached through IgnoreQueryFilters, because the filter hides it from reads -
            // proving the write guard stands on its own rather than relying on the filter.
            var other = await db.Vehicles.IgnoreQueryFilters()
                .FirstAsync(v => v.Id == seeded.Karachi);
            other.Model = "TAMPERED";

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    /// <summary>Case 4: tenant A's overlay is invisible to tenant B.</summary>
    [Fact]
    public async Task Tenant_overlay_is_invisible_to_another_tenant()
    {
        var seeded = await SeedCatalogAsync();
        var (nihonId, karachiId) = await TenantIdsAsync();

        long nihonOverlayId;

        var (writeScope, writeDb) = ScopedFor(_factory, nihonId);
        using (writeScope)
        {
            var overlay = await writeDb.TenantVehicles
                .FirstOrDefaultAsync(x => x.VehicleId == seeded.Global);

            if (overlay is null)
            {
                overlay = new TenantVehicle
                {
                    TenantId = nihonId,
                    VehicleId = seeded.Global,
                    TenantPrice = 1_234_567m,
                    TenantCurrencyCode = "JPY",
                };
                writeDb.TenantVehicles.Add(overlay);
                await writeDb.SaveChangesAsync();
            }

            nihonOverlayId = overlay.Id;
        }

        var (readScope, readDb) = ScopedFor(_factory, karachiId);
        using (readScope)
        {
            // Karachi sees the shared vehicle itself...
            Assert.Contains(seeded.Global, await readDb.Vehicles.Select(v => v.Id).ToListAsync());

            // ...but not Nihon's commercial state over it. Asserted against Nihon's specific
            // row rather than an empty set, because Karachi legitimately has overlays of its
            // own - including one on this same vehicle, written by another test in this class.
            var visible = await readDb.TenantVehicles.ToListAsync();
            Assert.DoesNotContain(nihonOverlayId, visible.Select(x => x.Id));
            Assert.All(visible, x => Assert.Equal(karachiId, x.TenantId));
        }
    }

    /// <summary>
    /// The overlay is what makes a shared catalog commercially usable: same car, different
    /// price per tenant, neither visible to the other.
    /// </summary>
    [Fact]
    public async Task Two_tenants_can_price_the_same_global_vehicle_independently()
    {
        var seeded = await SeedCatalogAsync();
        var (nihonId, karachiId) = await TenantIdsAsync();

        async Task PriceAsync(long tenantId, decimal price, string currency)
        {
            var (scope, db) = ScopedFor(_factory, tenantId);
            using (scope)
            {
                var existing = await db.TenantVehicles
                    .FirstOrDefaultAsync(x => x.VehicleId == seeded.Global);

                if (existing is null)
                {
                    db.TenantVehicles.Add(new TenantVehicle
                    {
                        TenantId = tenantId,
                        VehicleId = seeded.Global,
                        TenantPrice = price,
                        TenantCurrencyCode = currency,
                    });
                }
                else
                {
                    existing.TenantPrice = price;
                    existing.TenantCurrencyCode = currency;
                }

                await db.SaveChangesAsync();
            }
        }

        await PriceAsync(nihonId, 2_000_000m, "JPY");
        await PriceAsync(karachiId, 15_000m, "USD");

        foreach (var (tenantId, expected, currency) in new[]
                 {
                     (nihonId, 2_000_000m, "JPY"),
                     (karachiId, 15_000m, "USD"),
                 })
        {
            var (scope, db) = ScopedFor(_factory, tenantId);
            using (scope)
            {
                var overlay = Assert.Single(await db.TenantVehicles.ToListAsync());
                Assert.Equal(expected, overlay.TenantPrice);
                Assert.Equal(currency, overlay.TenantCurrencyCode);
            }
        }
    }
}
