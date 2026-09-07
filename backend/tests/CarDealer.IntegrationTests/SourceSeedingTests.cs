using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The seeder bootstraps an empty catalogue and then never touches the source list again.
/// </summary>
/// <remarks>
/// Reported from a real database: an operator deleted the sample sources, registered their own
/// two exporters, imported 99 real vehicles - and every restart put the deleted ones back.
/// Sources are operator data, so the platform may offer a starting set and may not re-impose
/// one on a catalogue somebody is already managing.
/// </remarks>
public sealed class SourceSeedingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SourceSeedingTests(ApiFactory factory) => _factory = factory;

    private async Task ReseedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

        await seeder.SeedAsync(includeDevelopmentUsers: true);
    }

    [Fact]
    public async Task A_deleted_source_stays_deleted_across_a_restart()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var goonet = await db.VehicleSources
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(s => s.Code == "goonet_exchange");

            // Present from the initial bootstrap; that part is not in dispute.
            Assert.NotNull(goonet);

            db.VehicleSources.Remove(goonet!);
            await db.SaveChangesAsync();
        }

        await ReseedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            Assert.False(
                await db.VehicleSources.IgnoreQueryFilters().AnyAsync(s => s.Code == "goonet_exchange"),
                "The seeder re-created a source the operator had deleted.");
        }
    }

    [Fact]
    public async Task Sources_the_operator_registered_are_left_exactly_as_they_are()
    {
        var code = $"own-{Guid.NewGuid():N}"[..14];

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            db.VehicleSources.Add(new VehicleSource
            {
                Name = "An exporter the operator added",
                Code = code,
                ProviderType = VehicleSourceProviderType.DealerJson,
                SourceType = VehicleSourceType.File,
                IsShared = true,
            });

            await db.SaveChangesAsync();
        }

        await ReseedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
            var mine = await db.VehicleSources.IgnoreQueryFilters().SingleAsync(s => s.Code == code);

            Assert.Equal("An exporter the operator added", mine.Name);
            Assert.Equal(VehicleSourceProviderType.DealerJson, mine.ProviderType);
            Assert.True(mine.IsActive);
        }
    }

    [Fact]
    public async Task An_empty_catalog_still_gets_a_starting_set()
    {
        // The other half of the rule. Removing the re-imposition must not leave a brand new
        // database with nothing to import into, or the documented walkthrough fails at step one.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var codes = await db.VehicleSources
            .IgnoreQueryFilters()
            .Select(s => s.Code)
            .ToListAsync();

        Assert.Contains("file-import", codes);
    }
}
