using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Duplicates;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Finding two catalogue rows that are one car, and letting a person act on it.
/// </summary>
/// <remarks>
/// Open item O15, whose whole point is a number: a real corpus of 104 listings from two Japanese
/// exporters contained twelve duplicate pairs and the platform matched none of them, because
/// neither exporter supplies a VIN and decision D3 auto-merges only on a strong identifier.
///
/// <para>
/// These tests are as much about what is <em>not</em> suggested as what is. A review queue is
/// only worth a person's time while its suggestions are mostly right; one that offers three
/// rival candidates for the same car teaches the reviewer to stop reading it, and then the
/// genuine duplicates go unmerged anyway.
/// </para>
/// </remarks>
public sealed class DuplicateDetectionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public DuplicateDetectionTests(ApiFactory factory) => _factory = factory;

    private sealed record CarSpec(
        int Year,
        int Mileage,
        string Colour,
        int? Cc = 1598,
        string Model = "Corolla Altis",
        Transmission Transmission = Transmission.Automatic,
        string? Vin = null,
        long? TenantId = null);

    /// <summary>
    /// Puts a set of cars in the catalogue, one listing each, and returns their ids in order.
    /// </summary>
    private async Task<(long SourceId, long[] VehicleIds)> SeedAsync(params CarSpec[] cars)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var source = new VehicleSource
        {
            Name = $"Dup source {Guid.NewGuid():N}"[..24],
            Code = $"dup-{Guid.NewGuid():N}"[..16],
            ProviderType = VehicleSourceProviderType.DealerJson,
            SourceType = VehicleSourceType.File,
            IsShared = true,
        };

        db.VehicleSources.Add(source);
        await db.SaveChangesAsync();

        var ids = new List<long>();

        foreach (var car in cars)
        {
            var vehicle = new Vehicle
            {
                PublicId = Guid.NewGuid(),
                TenantId = car.TenantId,
                Make = "TOYOTA",
                Model = car.Model,
                ModelYear = car.Year,
                Mileage = car.Mileage,
                MileageUnit = MileageUnit.Kilometers,
                ExteriorColor = car.Colour,
                EngineDisplacementCc = car.Cc,
                Transmission = car.Transmission,
                FuelType = FuelType.Petrol,
                SteeringSide = SteeringSide.RightHandDrive,
                BodyType = "Sedan",
                Vin = car.Vin,
                Status = VehicleStatus.Active,
            };

            db.Vehicles.Add(vehicle);
            await db.SaveChangesAsync();

            db.VehicleListings.Add(new VehicleListing
            {
                VehicleId = vehicle.Id,
                TenantId = car.TenantId,
                VehicleSourceId = source.Id,
                ExternalListingId = $"ext-{Guid.NewGuid():N}"[..20],
                Price = 5_720m,
                CurrencyCode = "USD",
                PriceType = PriceType.FreeOnBoard,
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
            });

            await db.SaveChangesAsync();
            ids.Add(vehicle.Id);
        }

        return (source.Id, [.. ids]);
    }

    private async Task<DuplicateScanResult> ScanAsync()
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<DuplicateScanService>()
            .ScanAsync();
    }

    private async Task<VehicleMatchCandidate?> CandidateFor(long left, long right)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var (lo, hi) = left < right ? (left, right) : (right, left);

        return await db.VehicleMatchCandidates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.VehicleId == lo && c.CandidateVehicleId == hi);
    }

    [Fact]
    public async Task Two_exporters_quoting_one_car_become_a_suggestion()
    {
        // The shape of all twelve pairs in the O15 corpus: same model, year, colour and an
        // odometer agreeing to the kilometre, with no VIN on either side.
        var (_, ids) = await SeedAsync(
            new CarSpec(2016, 56_455, "Gray"),
            new CarSpec(2016, 56_455, "GRAY"));

        await ScanAsync();

        var candidate = await CandidateFor(ids[0], ids[1]);

        Assert.NotNull(candidate);
        Assert.Equal(MatchCandidateStatus.Pending, candidate.Status);
        Assert.True(candidate.Score >= 0.5m);

        // The reasons travel with it. A reviewer archiving one of two cars for every tenant at
        // once needs to see what agreed, not just a number.
        Assert.False(string.IsNullOrWhiteSpace(candidate.SignalsJson));
        Assert.Contains("odometer", candidate.SignalsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_merged_by_the_scan_itself()
    {
        // Decision D3, and the reason this feature is a queue rather than a rule. Both rows are
        // still Active and still searchable after a scan.
        var (_, ids) = await SeedAsync(
            new CarSpec(2016, 61_311, "Black"),
            new CarSpec(2016, 61_311, "Black"));

        await ScanAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var statuses = await db.Vehicles
            .IgnoreQueryFilters()
            .Where(v => ids.Contains(v.Id))
            .Select(v => v.Status)
            .ToListAsync();

        Assert.All(statuses, s => Assert.Equal(VehicleStatus.Active, s));
        Assert.Empty(await db.VehicleMergeHistories
            .Where(h => ids.Contains(h.MergedVehicleId) && h.SurvivingVehicleId != h.MergedVehicleId)
            .ToListAsync());
    }

    [Fact]
    public async Task Near_new_cars_a_few_kilometres_apart_are_not_paired()
    {
        // The measurement that decided the design. One exporter in the corpus had four separate
        // 2026 cars of one colour reading 4, 9, 11 and 78 km; any tolerance band wide enough to
        // absorb a rounding difference makes those indistinguishable, and the reviewer is handed
        // three rival candidates for one car.
        var (_, ids) = await SeedAsync(
            new CarSpec(2026, 4, "Gray"),
            new CarSpec(2026, 9, "Gray"),
            new CarSpec(2026, 11, "Gray"));

        await ScanAsync();

        Assert.Null(await CandidateFor(ids[0], ids[1]));
        Assert.Null(await CandidateFor(ids[1], ids[2]));
        Assert.Null(await CandidateFor(ids[0], ids[2]));
    }

    [Fact]
    public async Task Delivery_mileage_cars_that_agree_exactly_are_still_paired()
    {
        // Four of the twelve genuine pairs are 2026 stock under 80 km. Excluding low odometers
        // outright would lose a third of the feature.
        var (_, ids) = await SeedAsync(
            new CarSpec(2026, 18, "Black"),
            new CarSpec(2026, 18, "Black"));

        await ScanAsync();

        Assert.NotNull(await CandidateFor(ids[0], ids[1]));
    }

    [Fact]
    public async Task One_exporter_listing_the_same_car_twice_is_found_too()
    {
        // Not a hypothetical: the O15 corpus contains one exporter offering the same car under
        // two stock numbers, which the lot-number hash deliberately keeps apart because they
        // genuinely are two listings. A scan restricted to cross-source pairs would miss it.
        var (_, ids) = await SeedAsync(
            new CarSpec(2021, 274_570, "White"),
            new CarSpec(2021, 274_570, "White"));

        await ScanAsync();

        Assert.NotNull(await CandidateFor(ids[0], ids[1]));
    }

    [Fact]
    public async Task Vehicles_owned_by_different_tenants_are_never_paired()
    {
        // The failure decision D1 exists to prevent. Two dealers each holding a private record
        // of an identical car own two different things, and pairing them offers a merge that
        // would attach one dealer's listing to the other's vehicle.
        var tenantId = await AnyTenantIdAsync();

        var (_, ids) = await SeedAsync(
            new CarSpec(2015, 88_123, "Silver"),
            new CarSpec(2015, 88_123, "Silver", TenantId: tenantId));

        await ScanAsync();

        Assert.Null(await CandidateFor(ids[0], ids[1]));
    }

    [Fact]
    public async Task A_pair_with_two_different_vins_is_never_suggested()
    {
        var (_, ids) = await SeedAsync(
            new CarSpec(2016, 71_002, "Blue", Vin: "JTNBA1HK9R3039064"),
            new CarSpec(2016, 71_002, "Blue", Vin: "JTNBA1HK9R3039065"));

        await ScanAsync();

        Assert.Null(await CandidateFor(ids[0], ids[1]));
    }

    [Fact]
    public async Task Rescanning_raises_nothing_new()
    {
        // The scan runs nightly against a catalogue that mostly has not changed, so a second
        // run must be a no-op. Without this the queue grows a fresh copy of every suggestion
        // every night.
        var (_, ids) = await SeedAsync(
            new CarSpec(2017, 93_450, "Red"),
            new CarSpec(2017, 93_450, "Red"));

        await ScanAsync();
        var second = await ScanAsync();

        Assert.Equal(0, second.CandidatesRaised);
        Assert.NotNull(await CandidateFor(ids[0], ids[1]));
    }

    [Fact]
    public async Task A_rejected_pair_does_not_come_back_tomorrow()
    {
        var (_, ids) = await SeedAsync(
            new CarSpec(2014, 120_500, "Green"),
            new CarSpec(2014, 120_500, "Green"));

        await ScanAsync();

        var candidate = await CandidateFor(ids[0], ids[1]);
        Assert.NotNull(candidate);

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var rejected = await client.PostAsync($"/api/v1/duplicates/{candidate.Id}/reject", null);
        Assert.Equal(HttpStatusCode.NoContent, rejected.StatusCode);

        await ScanAsync();

        var after = await CandidateFor(ids[0], ids[1]);
        Assert.NotNull(after);
        Assert.Equal(MatchCandidateStatus.Rejected, after.Status);
    }

    [Fact]
    public async Task Merging_moves_the_listings_and_archives_the_duplicate()
    {
        var (_, ids) = await SeedAsync(
            new CarSpec(2018, 44_311, "Pearl"),
            new CarSpec(2018, 44_311, "Pearl"));

        await ScanAsync();

        var candidate = await CandidateFor(ids[0], ids[1]);
        Assert.NotNull(candidate);

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/duplicates/{candidate.Id}/merge", new { note = "Same car, two feeds." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("listingsMoved").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        // The older row survives, because other records already point at it.
        var survivorId = ids[0];
        var absorbedId = ids[1];

        var survivor = await db.Vehicles.IgnoreQueryFilters().FirstAsync(v => v.Id == survivorId);
        var absorbed = await db.Vehicles.IgnoreQueryFilters().FirstAsync(v => v.Id == absorbedId);

        Assert.Equal(VehicleStatus.Active, survivor.Status);
        Assert.Equal(VehicleStatus.Archived, absorbed.Status);

        // Both offers now hang off the surviving car, which is the entire point: the cheaper
        // price becomes visible beside the dearer one instead of on a second screen.
        var offers = await db.VehicleListings
            .IgnoreQueryFilters()
            .CountAsync(l => l.VehicleId == survivorId);

        Assert.Equal(2, offers);
        Assert.Equal(0, await db.VehicleListings
            .IgnoreQueryFilters()
            .CountAsync(l => l.VehicleId == absorbedId));
    }

    [Fact]
    public async Task A_merge_can_be_undone()
    {
        // Under D1 a wrong merge is wrong for every tenant at once, and the person who notices
        // is rarely the person who did it. Reversal is not a nicety.
        var (_, ids) = await SeedAsync(
            new CarSpec(2013, 155_900, "Bronze"),
            new CarSpec(2013, 155_900, "Bronze"));

        await ScanAsync();

        var candidate = await CandidateFor(ids[0], ids[1]);
        Assert.NotNull(candidate);

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        await client.PostAsJsonAsync($"/api/v1/duplicates/{candidate.Id}/merge", new { });

        long mergeId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            mergeId = await db.VehicleMergeHistories
                .Where(h => h.MergedVehicleId == ids[1] && h.SurvivingVehicleId == ids[0])
                .Select(h => h.Id)
                .FirstAsync();
        }

        var reverted = await client.PostAsync($"/api/v1/duplicates/merges/{mergeId}/revert", null);
        Assert.Equal(HttpStatusCode.NoContent, reverted.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var absorbed = await db.Vehicles.IgnoreQueryFilters().FirstAsync(v => v.Id == ids[1]);
            Assert.Equal(VehicleStatus.Active, absorbed.Status);

            // Its own listing went back, and the survivor kept only its own.
            Assert.Equal(1, await db.VehicleListings
                .IgnoreQueryFilters().CountAsync(l => l.VehicleId == ids[1]));
            Assert.Equal(1, await db.VehicleListings
                .IgnoreQueryFilters().CountAsync(l => l.VehicleId == ids[0]));

            // Back to Pending, not Rejected: undoing a merge says it was wrong to merge, not
            // that the cars are definitely different.
            var candidateAfter = await db.VehicleMatchCandidates
                .FirstAsync(c => c.VehicleId == ids[0] && c.CandidateVehicleId == ids[1]);

            Assert.Equal(MatchCandidateStatus.Pending, candidateAfter.Status);
        }
    }

    [Fact]
    public async Task Merging_the_same_pair_twice_is_refused()
    {
        var (_, ids) = await SeedAsync(
            new CarSpec(2012, 201_400, "Beige"),
            new CarSpec(2012, 201_400, "Beige"));

        await ScanAsync();

        var candidate = await CandidateFor(ids[0], ids[1]);
        Assert.NotNull(candidate);

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var first = await client.PostAsJsonAsync($"/api/v1/duplicates/{candidate.Id}/merge", new { });
        var second = await client.PostAsJsonAsync($"/api/v1/duplicates/{candidate.Id}/merge", new { });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_salesperson_cannot_merge_anything()
    {
        // Merging edits the shared catalogue, so it sits with the source administration
        // permissions rather than with everyday selling.
        var (_, ids) = await SeedAsync(
            new CarSpec(2019, 33_700, "Navy"),
            new CarSpec(2019, 33_700, "Navy"));

        await ScanAsync();

        var candidate = await CandidateFor(ids[0], ids[1]);
        Assert.NotNull(candidate);

        var client = await _factory.AuthenticatedClientAsync("sales@nihon-motors.test");

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/duplicates")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"/api/v1/duplicates/{candidate.Id}/merge", new { })).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync($"/api/v1/duplicates/{candidate.Id}/reject", null)).StatusCode);
    }

    [Fact]
    public async Task The_queue_shows_both_cars_and_the_reasons()
    {
        var (_, ids) = await SeedAsync(
            new CarSpec(2016, 58_209, "Gray"),
            new CarSpec(2016, 58_209, "Gray"));

        await ScanAsync();

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/duplicates?pageSize=100");

        var mine = body.GetProperty("items").EnumerateArray().FirstOrDefault(i =>
            i.GetProperty("left").GetProperty("id").GetInt64() == ids[0]
            && i.GetProperty("right").GetProperty("id").GetInt64() == ids[1]);

        Assert.NotEqual(JsonValueKind.Undefined, mine.ValueKind);

        // Signals as a list, not as a JSON string the screen would have to parse itself.
        var signals = mine.GetProperty("signals").EnumerateArray().ToList();
        Assert.NotEmpty(signals);
        Assert.Contains(signals, s => s.GetProperty("name").GetString() == "odometer");

        // Each side carries its offers, which is what a reviewer compares.
        Assert.NotEmpty(mine.GetProperty("left").GetProperty("offers").EnumerateArray());
    }

    [Fact]
    public async Task A_pair_whose_car_was_already_archived_leaves_the_live_queue()
    {
        // Three vehicles sharing a blocking key make three pairs. Merging one archives a car,
        // and the pair still pointing at it stops being a question anybody can answer - the
        // remaining pair covers the same comparison properly.
        var (_, ids) = await SeedAsync(
            new CarSpec(2017, 66_612, "Copper"),
            new CarSpec(2017, 66_612, "Copper"),
            new CarSpec(2017, 66_612, "Copper"));

        await ScanAsync();

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        async Task<int> MineInQueue()
        {
            var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/duplicates?pageSize=100");

            return body.GetProperty("items").EnumerateArray().Count(i =>
                ids.Contains(i.GetProperty("left").GetProperty("id").GetInt64())
                && ids.Contains(i.GetProperty("right").GetProperty("id").GetInt64()));
        }

        Assert.Equal(3, await MineInQueue());

        var first = await CandidateFor(ids[0], ids[1]);
        Assert.NotNull(first);

        await client.PostAsJsonAsync($"/api/v1/duplicates/{first.Id}/merge", new { });

        // (0,1) is merged and (1,2) points at the archived car, so only (0,2) is left to ask.
        Assert.Equal(1, await MineInQueue());

        // The count must agree with the list. A badge saying twelve over a list showing ten
        // sends somebody looking for two suggestions that were never there.
        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/v1/duplicates?pageSize=1"))
            .GetProperty("totalCount").GetInt32();
        var counted = (await client.GetFromJsonAsync<JsonElement>("/api/v1/duplicates/count"))
            .GetProperty("pending").GetInt32();

        Assert.Equal(listed, counted);

        // The hidden one is still Pending, not silently rejected - reversing the merge brings
        // the car back and the question with it.
        var stale = await CandidateFor(ids[1], ids[2]);
        Assert.NotNull(stale);
        Assert.Equal(MatchCandidateStatus.Pending, stale.Status);
    }

    [Fact]
    public async Task Merging_a_duplicate_collapses_the_customer_s_two_alerts_into_one()
    {
        // The payoff, from the salesperson's side. A car listed twice raises two alerts for the
        // same customer - the alert scan cannot tell they are one car, which is the problem
        // being fixed - and merging has to be what makes the second one go away.
        var (_, ids) = await SeedAsync(
            new CarSpec(2020, 47_808, "Teal"),
            new CarSpec(2020, 47_808, "Teal"));

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = "Alerted",
            lastName = "Buyer",
            phone = "+81 90 5555 6666",
        });
        created.EnsureSuccessStatusCode();

        var customer = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();

        long requirementId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var tenantId = await db.Customers.IgnoreQueryFilters()
                .Where(c => c.PublicId == customer).Select(c => c.TenantId).FirstAsync();

            requirementId = await db.CustomerRequirements.IgnoreQueryFilters()
                .Where(r => r.Customer.PublicId == customer).Select(r => r.Id).FirstOrDefaultAsync();

            if (requirementId == 0)
            {
                var requirement = new CustomerRequirement
                {
                    TenantId = tenantId,
                    CustomerId = await db.Customers.IgnoreQueryFilters()
                        .Where(c => c.PublicId == customer).Select(c => c.Id).FirstAsync(),
                    Make = "TOYOTA",
                    Status = RequirementStatus.Open,
                };

                db.CustomerRequirements.Add(requirement);
                await db.SaveChangesAsync();
                requirementId = requirement.Id;
            }

            // One alert per copy, which is exactly what the hourly scan produces today.
            foreach (var vehicleId in ids)
            {
                db.RequirementAlerts.Add(new RequirementAlert
                {
                    TenantId = tenantId,
                    CustomerRequirementId = requirementId,
                    VehicleId = vehicleId,
                    MatchedAtUtc = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        await ScanAsync();

        var candidate = await CandidateFor(ids[0], ids[1]);
        Assert.NotNull(candidate);

        await client.PostAsJsonAsync($"/api/v1/duplicates/{candidate.Id}/merge", new { });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var remaining = await db.RequirementAlerts
                .IgnoreQueryFilters()
                .Where(a => a.CustomerRequirementId == requirementId)
                .ToListAsync();

            // One alert, pointing at the car that is still in the catalogue - not two, and not
            // one stranded on an archived row whose offers have moved away.
            Assert.Single(remaining);
            Assert.Equal(ids[0], remaining[0].VehicleId);
        }
    }

    private async Task<long> AnyTenantIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        return await db.Tenants.IgnoreQueryFilters().Select(t => t.Id).FirstAsync();
    }
}
