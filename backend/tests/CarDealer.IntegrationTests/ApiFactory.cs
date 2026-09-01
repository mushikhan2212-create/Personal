using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Hosts the API against a real SQL Server database, one per test class run.
/// </summary>
/// <remarks>
/// Acceptance criterion J5: integration tests run against real SQL Server, not the in-memory
/// provider. The in-memory provider ignores unique indexes, filtered indexes and persisted
/// computed columns - and the tenant-scope uniqueness this phase depends on is built from
/// exactly those. A green in-memory suite would prove nothing about the constraints that
/// matter.
/// </remarks>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = "CarDealerTests_" + Guid.NewGuid().ToString("N")[..12];

    public static string SqlServerHost =>
        Environment.GetEnvironmentVariable("CARDEALER_TEST_SQL_HOST") ?? "localhost,1433";

    public static string SqlServerPassword =>
        Environment.GetEnvironmentVariable("CARDEALER_TEST_SQL_PASSWORD") ?? "Dev_L0cal_Pass!2024";

    private string ConnectionString =>
        $"Server={SqlServerHost};Database={_databaseName};User Id=sa;Password={SqlServerPassword};"
        + "TrustServerCertificate=True;Encrypt=False";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["ConnectionStrings:Redis"] = string.Empty,
                ["Jwt:Issuer"] = "cardealer-tests",
                ["Jwt:Audience"] = "cardealer-tests",
                ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-chars-long",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "14",

                // Effectively disable throttling: a test class makes far more auth calls in a
                // second than any human, and criterion I2 is covered by its own test which
                // sets this deliberately low.
                ["RateLimits:Auth:PermitLimit"] = "10000",
                ["RateLimits:Auth:WindowSeconds"] = "60",
            }));

        return base.CreateHost(builder);
    }

    public Task InitializeAsync()
    {
        // Startup runs migrations and seeding, so touching the host is enough to build the
        // database. This also exercises criterion B1 on every test run.
        _ = Services;
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
        await db.Database.EnsureDeletedAsync();

        await base.DisposeAsync();
    }

    public HttpClient CreateApiClient() => CreateClient();

    // ---------------------------------------------------------------------
    // Helpers shared by the test classes
    // ---------------------------------------------------------------------

    public const string SeedPassword = DatabaseSeeder.DevelopmentPassword;

    public async Task<AuthPayload> LoginAsync(HttpClient client, string email, string? tenantSlug = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email,
            password = SeedPassword,
            tenantSlug,
        });

        var payload = await response.Content.ReadFromJsonAsync<AuthPayload>()
            ?? throw new InvalidOperationException("Login returned no body.");

        return payload with { StatusCode = (int)response.StatusCode };
    }

    public async Task<HttpClient> AuthenticatedClientAsync(string email, string? tenantSlug = null)
    {
        var client = CreateApiClient();
        var auth = await LoginAsync(client, email, tenantSlug);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return client;
    }

    public sealed record AuthPayload(
        bool RequiresTenantSelection,
        string? AccessToken,
        string? RefreshToken,
        JsonElement? ActiveTenant,
        JsonElement[]? AvailableTenants,
        string[]? Permissions)
    {
        public int StatusCode { get; init; }
    }
}
