using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Per-user source preferences: which sources feed one person's searches.
/// </summary>
/// <remarks>
/// The property worth guarding is that this is a view preference and nothing more. One user
/// muting a source must change that user's results and nobody else's - not a colleague in the
/// same tenant, and certainly not another tenant. Everything else here follows from that.
/// </remarks>
public sealed class MySourcesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public MySourcesTests(ApiFactory factory) => _factory = factory;

    private static MultipartFormDataContent Upload(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return new MultipartFormDataContent { { content, "file", "import.json" } };
    }

    private async Task<string> SourceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var code = $"pref-{Guid.NewGuid():N}"[..16];

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

    private static string Document(string marker, string model, string? vin = null)
        => JsonSerializer.Serialize(new
        {
            vehicles = new[]
            {
                new
                {
                    externalId = $"{model}-{Guid.NewGuid():N}"[..20],
                    make = marker,
                    model,
                    year = 2019,
                    price = 1_500_000,
                    currency = "JPY",
                    vin,
                    lastSeenAtUtc = DateTime.UtcNow,
                },
            },
        });

    private static async Task<int> CountAsync(HttpClient client, string marker)
    {
        var result = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        return result.GetProperty("totalCount").GetInt32();
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, string code, bool enabled)
        => client.PutAsJsonAsync($"/api/v1/me/sources/{code}", new { isEnabled = enabled });

    [Fact]
    public async Task Every_source_is_on_until_the_user_turns_one_off()
    {
        var code = await SourceAsync();
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/me/sources");
        var mine = listed.EnumerateArray().Single(s => s.GetProperty("code").GetString() == code);

        // A source nobody has touched is on. Otherwise an administrator would import 400 cars
        // and nobody would see them until every user opted in individually.
        Assert.True(mine.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task Muting_a_source_hides_its_cars_from_that_user_only()
    {
        var code = await SourceAsync();
        var marker = $"PRF{Guid.NewGuid():N}"[..10];

        var aiko = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        await aiko.PostAsync($"/api/v1/vehicle-sources/{code}/import", Upload(Document(marker, "Hiace")));

        var kenji = await _factory.AuthenticatedClientAsync("sales@nihon-motors.test");

        Assert.Equal(1, await CountAsync(aiko, marker));
        Assert.Equal(1, await CountAsync(kenji, marker));

        (await SetAsync(kenji, code, false)).EnsureSuccessStatusCode();

        // Kenji's own view changes...
        Assert.Equal(0, await CountAsync(kenji, marker));

        // ...and Aiko, in the same tenant, is untouched. This is the whole point: a view
        // preference must never remove a colleague's stock.
        Assert.Equal(1, await CountAsync(aiko, marker));
    }

    [Fact]
    public async Task Re_enabling_brings_the_cars_back()
    {
        var code = await SourceAsync();
        var marker = $"PRF{Guid.NewGuid():N}"[..10];

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        await client.PostAsync($"/api/v1/vehicle-sources/{code}/import", Upload(Document(marker, "Note")));

        await SetAsync(client, code, false);
        Assert.Equal(0, await CountAsync(client, marker));

        await SetAsync(client, code, true);
        Assert.Equal(1, await CountAsync(client, marker));

        // Enabled is the default, so the row is removed rather than stored as a redundant true.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        var source = await db.VehicleSources.IgnoreQueryFilters().FirstAsync(s => s.Code == code);

        Assert.False(await db.UserVehicleSourcePreferences
            .AnyAsync(p => p.VehicleSourceId == source.Id));
    }

    [Fact]
    public async Task A_car_a_second_enabled_source_also_lists_stays_visible()
    {
        var muted = await SourceAsync();
        var kept = await SourceAsync();
        var marker = $"PRF{Guid.NewGuid():N}"[..10];
        var vin = $"JT{Guid.NewGuid():N}"[..17];

        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        // The same physical car offered by both, so it merges into one vehicle.
        await client.PostAsync($"/api/v1/vehicle-sources/{muted}/import", Upload(Document(marker, "Prado", vin)));
        await client.PostAsync($"/api/v1/vehicle-sources/{kept}/import", Upload(Document(marker, "Prado", vin)));

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(1, before.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, before.GetProperty("items")[0].GetProperty("offerCount").GetInt32());

        await SetAsync(client, muted, false);

        // The car survives on the remaining source's listing, with one fewer offer. Muting one
        // source must not remove stock a different enabled source is supplying.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/vehicles?q={marker}");
        Assert.Equal(1, after.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, after.GetProperty("items")[0].GetProperty("offerCount").GetInt32());
    }

    [Fact]
    public async Task A_read_only_user_may_manage_their_own_sources()
    {
        var code = await SourceAsync();
        var marker = $"PRF{Guid.NewGuid():N}"[..10];

        var owner = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        await owner.PostAsync($"/api/v1/vehicle-sources/{code}/import", Upload(Document(marker, "Fit")));

        // Read-only cannot import, but this is a view preference and changes nothing for
        // anyone else, so withholding it would be pointless.
        var mei = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        Assert.Equal(HttpStatusCode.OK, (await mei.GetAsync("/api/v1/me/sources")).StatusCode);
        Assert.Equal(1, await CountAsync(mei, marker));

        (await SetAsync(mei, code, false)).EnsureSuccessStatusCode();
        Assert.Equal(0, await CountAsync(mei, marker));

        // And the owner is unaffected by a read-only user's preference.
        Assert.Equal(1, await CountAsync(owner, marker));
    }

    [Fact]
    public async Task A_preference_cannot_be_set_for_a_source_the_user_cannot_see()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await SetAsync(client, "no-such-source", false);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
