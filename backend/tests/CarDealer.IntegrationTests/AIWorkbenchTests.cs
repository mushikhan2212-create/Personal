using System.Net;
using System.Net.Http.Json;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The development-only endpoint for trying a message against a model.
/// </summary>
/// <remarks>
/// Its reason to exist is tuning, so what is worth pinning down here is not that it extracts -
/// that is the real endpoint's job and is tested there - but that it cannot be reached by
/// somebody who should not be spending the account's money. It takes a model id from the caller,
/// which makes the permission matter more here than anywhere else.
/// </remarks>
public sealed class AIWorkbenchTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AIWorkbenchTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_read_only_user_cannot_spend_money_here_either()
    {
        var client = await _factory.AuthenticatedClientAsync("readonly@nihon-motors.test");

        var response = await client.PostAsJsonAsync(
            "/api/v1/ai/read-message", new { message = "Corolla Axio under 4000 USD" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/ai/read-message", new { message = "Corolla Axio under 4000 USD" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_message_is_refused_before_anything_is_spent()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var response = await client.PostAsJsonAsync(
            "/api/v1/ai/read-message", new { message = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
