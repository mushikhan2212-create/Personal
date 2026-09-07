using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Infrastructure.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The POC evaluation measurements (master prompt section 8).
/// </summary>
/// <remarks>
/// These exist because the report is built from grouped aggregates with conditional sums, and
/// that is the shape of query that compiles happily and then throws at runtime when EF cannot
/// translate it - which has already happened once on this project, to a search query that every
/// test passed. A report nobody can run is worse than no report, so it is exercised against
/// real SQL Server here rather than assumed to work.
/// </remarks>
public sealed class CatalogReportTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CatalogReportTests(ApiFactory factory) => _factory = factory;

    /// <summary>Seeds one source whose field coverage is known exactly.</summary>
    private async Task<string> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var code = $"rep-{Guid.NewGuid():N}"[..14];

        var source = new VehicleSource
        {
            Name = $"Report source {code}",
            Code = code,
            ProviderType = VehicleSourceProviderType.DealerJson,
            SourceType = VehicleSourceType.File,
            IsShared = true,
        };

        db.VehicleSources.Add(source);
        await db.SaveChangesAsync();

        // Four listings: two with a VIN, one chassis-only, one with no identifier at all.
        // Two carry an incoterm. That makes every percentage below a whole number nobody has
        // to squint at - 50% means two of four, not "about half".
        (string? Vin, string? Chassis, PriceType Incoterm)[] plan =
        [
            ("JTDBT923771000001", null, PriceType.FreeOnBoard),
            ("JTDBT923771000002", null, PriceType.CostInsuranceFreight),
            (null, "NCP81-0012345", PriceType.Unknown),
            (null, null, PriceType.Unknown),
        ];

        foreach (var (vin, chassis, incoterm) in plan)
        {
            var vehicle = new Vehicle
            {
                PublicId = Guid.NewGuid(),
                Make = "Toyota",
                Model = "Probox",
                ModelYear = 2019,
                Mileage = 90_000,
                Vin = vin,
                ChassisNumber = chassis,
                SteeringSide = SteeringSide.RightHandDrive,
                Status = VehicleStatus.Active,
            };

            db.Vehicles.Add(vehicle);
            db.VehicleListings.Add(new VehicleListing
            {
                Vehicle = vehicle,
                VehicleSourceId = source.Id,
                ExternalListingId = $"{code}-{Guid.NewGuid():N}"[..24],
                Price = 750_000m,
                CurrencyCode = "JPY",
                PriceType = incoterm,
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();
        return code;
    }

    [Fact]
    public async Task The_report_runs_against_real_sql_and_measures_each_source()
    {
        var code = await SeedAsync();

        using var scope = _factory.Services.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<CatalogReportService>();

        var report = await reports.BuildAsync();
        var mine = report.Sources.Single(s => s.Code == code);

        Assert.Equal(4, mine.Listings);

        // Two of four carry a VIN, one a chassis number, one nothing at all.
        Assert.Equal(50, mine.Identity.VinPercent);
        Assert.Equal(25, mine.Identity.ChassisPercent);
        Assert.Equal(25, mine.Identity.NoIdentifierPercent);

        // Counted directly rather than inferred by subtracting the three coverage figures,
        // which would double-count a listing carrying two identifiers.
        Assert.Equal(100, mine.Completeness["make"]);
        Assert.Equal(50, mine.Completeness["incoterm"]);
        Assert.Equal(100, mine.Completeness["price"]);
        Assert.Equal(0, mine.Completeness["photos"]);
    }

    [Fact]
    public async Task Every_required_measurement_is_present()
    {
        // Section 8 names completeness, freshness, response time, duplicates and images. A
        // report missing one of them does not satisfy the criterion, however good the rest is.
        await SeedAsync();

        using var scope = _factory.Services.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<CatalogReportService>();

        var report = await reports.BuildAsync();

        Assert.True(report.Totals.Vehicles > 0);
        Assert.NotEmpty(report.Sources);
        Assert.All(report.Sources, s => Assert.True(s.Freshness.NewestSeenUtc >= s.Freshness.OldestSeenUtc));

        // Five realistic searches, timed - the wording of the criterion, not a round number.
        Assert.Equal(5, report.Searches.Count);
        Assert.All(report.Searches, t => Assert.True(t.ElapsedMs >= 0));

        Assert.True(report.Deduplication.VehiclesTotal > 0);
    }

    [Fact]
    public async Task Deduplication_counts_a_car_offered_by_two_sources()
    {
        // The number the POC exists to produce. Without it the report can say how many cars
        // there are but not whether aggregating several exporters is worth anything.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var vin = $"JH4KA7532NC{Random.Shared.Next(100000, 999999)}";
        var vehicle = new Vehicle
        {
            PublicId = Guid.NewGuid(),
            Make = "Honda",
            Model = "Fit",
            Vin = vin,
            Status = VehicleStatus.Active,
        };

        db.Vehicles.Add(vehicle);

        foreach (var suffix in new[] { "one", "two" })
        {
            var source = new VehicleSource
            {
                Name = $"Dup {suffix}",
                Code = $"dup-{suffix}-{Guid.NewGuid():N}"[..18],
                ProviderType = VehicleSourceProviderType.DealerJson,
                SourceType = VehicleSourceType.File,
                IsShared = true,
            };

            db.VehicleSources.Add(source);
            db.VehicleListings.Add(new VehicleListing
            {
                Vehicle = vehicle,
                VehicleSource = source,
                ExternalListingId = $"{suffix}-{Guid.NewGuid():N}"[..24],
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();

        var reports = scope.ServiceProvider.GetRequiredService<CatalogReportService>();
        var report = await reports.BuildAsync();

        Assert.True(report.Deduplication.OfferedByMoreThanOneSource >= 1);
        Assert.True(report.Deduplication.IdentifiableByVin >= 1);
    }

    [Fact]
    public async Task Reading_the_catalog_is_not_enough_to_read_the_report()
    {
        // Per-source data quality is operator information: which exporter supplies incoterms
        // and which does not is a commercial fact about a supplier, not a browsing feature.
        var reader = await _factory.AuthenticatedClientAsync("sales@nihon-motors.test");
        var admin = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/catalog-report")).StatusCode);

        var allowed = await admin.GetAsync("/api/v1/catalog-report");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // Proves the whole pipeline survives JSON serialization, not just the service call.
        var body = await allowed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("searches").GetArrayLength() == 5);
    }
}
