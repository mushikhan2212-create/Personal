using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Application.AI;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarDealer.IntegrationTests;

/// <summary>
/// A provider whose answer the test writes.
/// </summary>
/// <remarks>
/// The reason CI never spends money. Every behaviour worth pinning down here - the fallback,
/// the guards firing, the persistence, the reuse, the permission - is a property of the code
/// around the call rather than of the model, so a scripted answer exercises all of it and a
/// real one would only add cost and non-determinism. Whether the ranking is any *good* is a
/// different question, answered by an eval run deliberately and not in a test suite.
/// </remarks>
internal sealed class ScriptedAIProvider : IAIProvider
{
    public string Name => "scripted";

    public string? Model => "scripted-model";

    public bool IsConfigured { get; set; } = true;

    public Func<RankingRequest, AIRankingResult> Answer { get; set; } =
        _ => AIRankingResult.Failed("nothing scripted");

    public int Calls { get; private set; }

    public Task<AIRankingResult> RankAsync(RankingRequest request, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Answer(request));
    }
}

public sealed class RecommendationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RecommendationTests(ApiFactory factory) => _factory = factory;

    private static string Marker() => "RC" + new string([.. Guid.NewGuid().ToByteArray().Take(8)
        .Select(b => (char)('a' + (b % 16)))]);

    /// <summary>
    /// A host whose AI provider is the scripted one, sharing this fixture's database.
    /// </summary>
    /// <remarks>
    /// WithWebHostBuilder returns a plain WebApplicationFactory rather than an ApiFactory, so
    /// the sign-in helper has to be reached through the fixture. The database is the same one:
    /// the connection string is built from a field on the fixture instance, and the derived
    /// host is configured by the same CreateHost.
    /// </remarks>
    private (WebApplicationFactory<Program> Factory, ScriptedAIProvider Provider) WithScripted()
    {
        var provider = new ScriptedAIProvider();

        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAIProvider>();
                services.AddScoped<IAIProvider>(_ => provider);
            }));

        return (factory, provider);
    }

    /// <summary>Signs in against a derived host, using the fixture's own login helper.</summary>
    private async Task<HttpClient> SignedInAsync(
        WebApplicationFactory<Program> factory, string email)
    {
        var client = factory.CreateClient();
        var auth = await _factory.LoginAsync(client, email);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return client;
    }

    private async Task<(Guid Customer, long Requirement, Guid[] Vehicles)> ScenarioAsync(
        HttpClient client, string marker)
    {
        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            firstName = $"{marker}Imran",
            lastName = "Sheikh",
            phone = $"+92 300 {Guid.NewGuid().ToString("N")[..7]}",
        });

        created.EnsureSuccessStatusCode();

        var customer = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetGuid();

        var vehicles = await AddVehiclesAsync(marker);

        var added = await client.PostAsJsonAsync(
            $"/api/v1/customers/{customer}/requirements",
            new { name = "Corolla wanted", make = $"{marker}Toyota" });

        added.EnsureSuccessStatusCode();

        var requirement = (await added.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt64();

        return (customer, requirement, vehicles);
    }

    private async Task<Guid[]> AddVehiclesAsync(string marker)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var source = await db.VehicleSources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Code == "rank-test-source");

        if (source is null)
        {
            source = new VehicleSource
            {
                Name = "Ranking test source",
                Code = "rank-test-source",
                ProviderType = VehicleSourceProviderType.DealerJson,
                SourceType = VehicleSourceType.File,
                IsShared = true,
            };

            db.VehicleSources.Add(source);
            await db.SaveChangesAsync();
        }

        var ids = new List<Guid>();

        // Priced so the deterministic order (cheapest first) is the opposite of the ranking the
        // tests script. Without that, a test could pass while the model's answer was ignored.
        foreach (var (year, mileage, price) in new[]
        {
            (2016, 62_620, 5_400m),
            (2017, 48_000, 5_900m),
            (2015, 91_000, 6_400m),
        })
        {
            var vehicle = new Vehicle
            {
                Make = $"{marker}Toyota",
                Model = "Corolla",
                ModelYear = year,
                Mileage = mileage,
                Status = VehicleStatus.Active,
            };

            db.Vehicles.Add(vehicle);
            db.VehicleListings.Add(new VehicleListing
            {
                Vehicle = vehicle,
                VehicleSourceId = source.Id,
                ExternalListingId = $"{marker}-{Guid.NewGuid():N}"[..24],
                Price = price,
                CurrencyCode = "USD",
                PriceBaseCurrency = price,
                BaseCurrencyCode = "USD",
                PriceType = PriceType.FreeOnBoard,
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                IsActive = true,
            });

            await db.SaveChangesAsync();
            ids.Add(vehicle.PublicId);
        }

        return [.. ids];
    }

    /// <summary>Scripts a well-formed ranking that simply reverses the candidate order.</summary>
    private static AIRankingResult Reversed(RankingRequest request)
        => new()
        {
            Ranked = [.. request.Candidates.Reverse().Select((c, i) => new RankedVehicle
            {
                Id = c.Id,
                Rank = i + 1,
                Score = 0.9m - (i * 0.1m),
                Reasons = ["scripted"],
            })],
            Usage = new AIUsage { InputTokens = 1_000, OutputTokens = 400 },
            Provider = "scripted",
            Model = "scripted-model",
        };

    private static async Task<JsonElement> RankAsync(
        HttpClient client, Guid customer, long requirement, bool refresh = false)
    {
        var response = await client.PostAsync(
            $"/api/v1/customers/{customer}/requirements/{requirement}/rank?refresh={refresh}", null);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task With_no_provider_configured_it_falls_back_and_says_so()
    {
        // The state this ships in. Nobody has a key yet, and the screen has to be honest about
        // that rather than looking broken.
        var m = Marker();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        var result = await RankAsync(client, customer, requirement);

        Assert.Equal("Deterministic", result.GetProperty("source").GetString());
        Assert.False(result.GetProperty("providerConfigured").GetBoolean());
        Assert.Contains("configured", result.GetProperty("notice").GetString());

        // Still a usable list. Falling back must never mean falling over.
        Assert.Equal(3, result.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_sound_ranking_is_used_and_stored()
    {
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Answer = Reversed;

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        var result = await RankAsync(client, customer, requirement);

        Assert.Equal("Ai", result.GetProperty("source").GetString());

        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items[0].GetProperty("rank").GetInt32());
        Assert.Equal("scripted", items[0].GetProperty("reasons")[0].GetString());

        // The model's order, not the filter's. The scripted answer reverses the cheapest-first
        // order, so the dearest car is now first - which is the whole point of the feature and
        // would be invisible if the deterministic list happened to agree.
        Assert.Equal(
            6_400m,
            items[0].GetProperty("vehicle").GetProperty("priceBaseCurrency").GetDecimal());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var stored = await db.VehicleRecommendations.IgnoreQueryFilters()
            .Where(r => r.CustomerRequirementId == requirement)
            .ToListAsync();

        Assert.Equal(3, stored.Count);
        Assert.All(stored, r => Assert.Equal(RecommendationSource.Ai, r.Source));

        var audit = await db.AIRequests.IgnoreQueryFilters()
            .OrderByDescending(r => r.Id)
            .FirstAsync();

        Assert.Equal(AIRequestStatus.Succeeded, audit.Status);
        Assert.Equal("rank", audit.Operation);
    }

    [Fact]
    public async Task An_invented_vehicle_is_rejected_and_recorded()
    {
        // The guard doing its job end to end. The salesperson sees a usable list; the audit row
        // records that the provider was paid for an answer nobody could use, which is the
        // number that decides whether to keep paying.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Answer = request => new AIRankingResult
        {
            Ranked =
            [
                new RankedVehicle
                {
                    Id = Guid.NewGuid(),
                    Rank = 1,
                    Score = 1m,
                    Reasons = ["a car nobody has"],
                },
                .. request.Candidates.Select((c, i) => new RankedVehicle
                {
                    Id = c.Id,
                    Rank = i + 2,
                    Score = 0.5m,
                    Reasons = ["real"],
                }),
            ],
            Usage = new AIUsage { InputTokens = 900, OutputTokens = 300 },
        };

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        var result = await RankAsync(client, customer, requirement);

        Assert.Equal("Deterministic", result.GetProperty("source").GetString());
        Assert.Contains("never sent", result.GetProperty("notice").GetString());
        Assert.Equal(3, result.GetProperty("items").GetArrayLength());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var audit = await db.AIRequests.IgnoreQueryFilters()
            .OrderByDescending(r => r.Id)
            .FirstAsync();

        // Rejected, not Failed. A provider that answers promptly and wrongly is a different
        // problem from one that does not answer, and only this distinction tells them apart.
        Assert.Equal(AIRequestStatus.Rejected, audit.Status);
        Assert.Contains("never sent", audit.FailureReason);

        // Nothing stored: a rejected ranking must not leave rows behind that later read as one
        // somebody paid for and saw.
        Assert.Empty(await db.VehicleRecommendations.IgnoreQueryFilters()
            .Where(r => r.CustomerRequirementId == requirement)
            .ToListAsync());
    }

    [Fact]
    public async Task A_hallucinated_mileage_is_rejected()
    {
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Answer = request => new AIRankingResult
        {
            Ranked = [.. request.Candidates.Select((c, i) => new RankedVehicle
            {
                Id = c.Id,
                Rank = i + 1,
                Score = 0.5m,

                // A figure belonging to no car in the set. Reads perfectly, and is false.
                Reasons = ["only 12,345 km"],
            })],
        };

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        var result = await RankAsync(client, customer, requirement);

        Assert.Equal("Deterministic", result.GetProperty("source").GetString());
        Assert.Contains("12,345", result.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task A_provider_that_throws_falls_back_rather_than_erroring()
    {
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Answer = _ => throw new HttpRequestException("the network went away");

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        // A 200 with a usable list, not a 500. A slow network is not a broken screen.
        var result = await RankAsync(client, customer, requirement);

        Assert.Equal("Deterministic", result.GetProperty("source").GetString());
        Assert.Equal(3, result.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Asking_the_same_question_twice_reuses_the_stored_answer()
    {
        // Cost control, and something stronger: a salesperson who refreshes must see the same
        // order they quoted from, not a fresh opinion.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Answer = Reversed;

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        var first = await RankAsync(client, customer, requirement);
        Assert.False(first.GetProperty("reused").GetBoolean());

        var callsAfterFirst = provider.Calls;

        var second = await RankAsync(client, customer, requirement);

        Assert.True(second.GetProperty("reused").GetBoolean());
        Assert.Equal(callsAfterFirst, provider.Calls);

        Assert.Equal(
            first.GetProperty("items")[0].GetProperty("vehicle").GetProperty("id").GetGuid(),
            second.GetProperty("items")[0].GetProperty("vehicle").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Refresh_asks_again()
    {
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Answer = Reversed;

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        await RankAsync(client, customer, requirement);
        var callsAfterFirst = provider.Calls;

        var again = await RankAsync(client, customer, requirement, refresh: true);

        Assert.Equal(callsAfterFirst + 1, provider.Calls);
        Assert.False(again.GetProperty("reused").GetBoolean());
    }

    [Fact]
    public async Task A_read_only_user_cannot_spend_money()
    {
        var m = Marker();
        var owner = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(owner, m);

        var readOnly = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var response = await readOnly.PostAsync(
            $"/api/v1/customers/{customer}/requirements/{requirement}/rank", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_rank_someone_elses_requirement()
    {
        var m = Marker();
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(nihon, m);

        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        // A 404 rather than a 403: confirming the id exists would leak that somebody has a
        // customer looking for this car.
        var response = await karachi.PostAsync(
            $"/api/v1/customers/{customer}/requirements/{requirement}/rank", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task No_customer_identity_is_ever_put_in_the_request()
    {
        // The O4 guarantee, asserted rather than trusted. RequirementBrief has no field for a
        // name - this proves the pipeline that fills it agrees, and would fail the day somebody
        // widened the type.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        RankingRequest? seen = null;

        provider.Answer = request =>
        {
            seen = request;
            return Reversed(request);
        };

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        await RankAsync(client, customer, requirement);

        Assert.NotNull(seen);

        var payload = JsonSerializer.Serialize(seen);

        Assert.DoesNotContain($"{m}Imran", payload);
        Assert.DoesNotContain("Sheikh", payload);
        Assert.DoesNotContain("+92", payload);
    }

    [Fact]
    public async Task A_car_that_only_just_meets_a_limit_goes_last_however_the_model_ranked_it()
    {
        // The broker's rule, end to end. The model is scripted to put the car nearest the
        // customer's mileage ceiling first - which is what real models did, three prompt
        // revisions running - and it has to come back last anyway.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        RankingRequest? seen = null;

        provider.Answer = request =>
        {
            seen = request;
            return Reversed(request);
        };

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        // 95,000 makes the 91,000 km car tight and leaves the other two comfortable. It is also
        // the dearest of the three, so nothing here can pass by accident on price order.
        var updated = await client.PutAsJsonAsync(
            $"/api/v1/customers/{customer}/requirements/{requirement}",
            new { name = "Corolla wanted", make = $"{m}Toyota", maxMileage = 95_000 });

        updated.EnsureSuccessStatusCode();

        var result = await RankAsync(client, customer, requirement, refresh: true);

        Assert.Equal("Ai", result.GetProperty("source").GetString());

        // The model was told, so it can say so in the reasons.
        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Candidates.Count(c => c.CloseToTheirLimits == true));
        Assert.Equal(91_000, seen.Candidates.Single(c => c.CloseToTheirLimits == true).Mileage);

        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        static int Mileage(JsonElement item)
            => item.GetProperty("vehicle").GetProperty("mileage").GetInt32();

        // Reversed sends it back at rank 1; it is shown at rank 3.
        Assert.Equal(91_000, Mileage(items[2]));
        Assert.Equal(3, items[2].GetProperty("rank").GetInt32());

        // And the two that fit comfortably keep the order the model chose for them, rather than
        // being re-sorted into something the code preferred.
        Assert.Equal([48_000, 62_620], items.Take(2).Select(Mileage));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        // Stored as shown, not as answered: what a salesperson saw is the thing worth keeping.
        var stored = await db.VehicleRecommendations.IgnoreQueryFilters()
            .Where(r => r.CustomerRequirementId == requirement)
            .OrderBy(r => r.Rank)
            .Select(r => r.Vehicle!.Mileage)
            .ToListAsync();

        Assert.Equal([48_000, 62_620, 91_000], stored);
    }

    [Fact]
    public async Task With_no_limits_stated_the_models_order_stands()
    {
        // The other half of the rule, and the one that stops it being a sort. A customer who
        // named no ceiling has nothing for the bands to separate, so nothing may move.
        var m = Marker();
        var (factory, provider) = WithScripted();
        using var _ = factory;

        provider.Answer = Reversed;

        var client = await SignedInAsync(factory, "owner@nihon-motors.test");
        var (customer, requirement, _) = await ScenarioAsync(client, m);

        var result = await RankAsync(client, customer, requirement);

        var mileages = result.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("vehicle").GetProperty("mileage").GetInt32())
            .ToList();

        // Cheapest-first is 62,620 / 48,000 / 91,000; Reversed is exactly that backwards.
        Assert.Equal([91_000, 48_000, 62_620], mileages);
    }
}
