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
/// Free-text search over make, model and variant.
/// </summary>
/// <remarks>
/// These exist because of a defect found by typing a real search into the real screen:
/// "Toyota 86 GT Limited" returned nothing for a car that was in the table. The phrase was
/// matched against each column whole, and no single column contains all four words - Make is
/// "Toyota", Model is "86", Variant is "GT Limited". Every individual word found the car; the
/// way a person actually types did not.
/// </remarks>
public sealed class SearchTextTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SearchTextTests(ApiFactory factory) => _factory = factory;

    /// <summary>Seeds one car whose name is split across all three searchable columns.</summary>
    private async Task<(string Marker, long TenantId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var tenantId = await db.Tenants.OrderBy(t => t.Id).Select(t => t.Id).FirstAsync();

        // Unique per run so a shared database cannot let one test see another's rows.
        var marker = $"TX{Guid.NewGuid():N}"[..10];

        var source = new VehicleSource
        {
            Name = "SBT Japan",
            Code = $"src-{Guid.NewGuid():N}"[..16],
            ProviderType = VehicleSourceProviderType.Carapis,
            SourceType = VehicleSourceType.Api,
            IsShared = true,
        };
        db.VehicleSources.Add(source);
        await db.SaveChangesAsync();

        async Task AddAsync(string make, string model, string? variant)
        {
            var vehicle = new Vehicle
            {
                PublicId = Guid.NewGuid(),
                Make = make,
                Model = model,
                Variant = variant,
                ModelYear = 2019,
                Status = VehicleStatus.Active,
            };

            db.Vehicles.Add(vehicle);
            db.VehicleListings.Add(new VehicleListing
            {
                Vehicle = vehicle,
                VehicleSourceId = source.Id,
                ExternalListingId = $"{model}-{Guid.NewGuid():N}"[..24],
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        await AddAsync($"{marker}Toyota", "86", "GT Limited");
        await AddAsync($"{marker}Toyota", "Hiace", "DX");

        // A car whose model contains a LIKE wildcard, to prove the escaping is real.
        await AddAsync($"{marker}Toyota", "100%Electric", null);

        return (marker, tenantId);
    }

    private async Task<int> CountAsync(string text, long tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var result = await new SqlServerSearchProvider(db)
            .SearchAsync(new VehicleSearchQuery { Text = text, PageSize = 100 });

        return result.TotalCount;
    }

    [Fact]
    public async Task A_phrase_spanning_make_model_and_variant_finds_the_car()
    {
        var (marker, tenantId) = await SeedAsync();

        // The reported failure, in the order a person types it.
        Assert.Equal(1, await CountAsync($"{marker}Toyota 86 GT Limited", tenantId));
    }

    [Fact]
    public async Task Word_order_does_not_matter()
    {
        var (marker, tenantId) = await SeedAsync();

        // Terms are ANDed independently, so nobody has to guess the column order.
        Assert.Equal(1, await CountAsync($"Limited 86 {marker}Toyota", tenantId));
    }

    [Fact]
    public async Task Every_word_has_to_match_something()
    {
        var (marker, tenantId) = await SeedAsync();

        // Both cars are this make, so the make alone finds all three.
        Assert.Equal(3, await CountAsync($"{marker}Toyota", tenantId));

        // Adding a word narrows rather than widens - an OR across terms would return 3 here,
        // which would make every extra word the user types useless.
        Assert.Equal(1, await CountAsync($"{marker}Toyota Hiace", tenantId));

        // And a word that matches nothing rules the row out entirely.
        Assert.Equal(0, await CountAsync($"{marker}Toyota Hiace Supra", tenantId));
    }

    [Fact]
    public async Task Extra_whitespace_is_not_a_search_term()
    {
        var (marker, tenantId) = await SeedAsync();

        Assert.Equal(1, await CountAsync($"  {marker}Toyota   86  ", tenantId));
    }

    [Fact]
    public async Task Wildcards_typed_by_the_user_are_matched_literally()
    {
        var (marker, tenantId) = await SeedAsync();

        // Unescaped, "%" is "match anything", so this would return every car of this make
        // instead of the one actually named that way. A user searching a literal percent sign
        // would have no way to tell a broken filter from a popular model.
        Assert.Equal(1, await CountAsync($"{marker}Toyota 100%Electric", tenantId));

        // A bare wildcard matches the one row that really contains the character.
        Assert.Equal(1, await CountAsync($"{marker}Toyota %", tenantId));

        // Underscore is the single-character wildcard; "8_" must not match "86".
        Assert.Equal(0, await CountAsync($"{marker}Toyota 8_", tenantId));
    }
}
