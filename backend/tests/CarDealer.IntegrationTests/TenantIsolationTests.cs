using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The four isolation cases required by schema delta section 1.4 and acceptance section C.
/// </summary>
/// <remarks>
/// Under decision D1 the query filter admits rows where TenantId is null, which is a weaker
/// guard than a flat equality. Decision D10 removes the UI, so nobody will spot a leak by
/// eye. These tests are the primary evidence that tenant isolation holds.
/// </remarks>
public sealed class TenantIsolationTests : IClassFixture<ApiFactory>
{
    private const string Nihon = "nihon-motors";
    private const string Karachi = "karachi-auto";

    private readonly ApiFactory _factory;

    public TenantIsolationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_records()
    {
        var nihonClient = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var karachiClient = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        var nihonMembers = await GetEmailsAsync(nihonClient);
        var karachiMembers = await GetEmailsAsync(karachiClient);

        Assert.NotEmpty(nihonMembers);
        Assert.NotEmpty(karachiMembers);

        Assert.Contains("owner@nihon-motors.test", nihonMembers);
        Assert.DoesNotContain("owner@nihon-motors.test", karachiMembers);
        Assert.Contains("owner@karachi-auto.test", karachiMembers);
        Assert.DoesNotContain("owner@karachi-auto.test", nihonMembers);
    }

    [Fact]
    public async Task Client_supplied_tenant_id_is_ignored()
    {
        var karachiClient = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");
        var baseline = await GetEmailsAsync(karachiClient);

        // Every plausible injection point. None of them is read: the tenant comes from the
        // validated token alone (SQL schema spec section 9).
        karachiClient.DefaultRequestHeaders.Add("X-Tenant-Id", "1");
        karachiClient.DefaultRequestHeaders.Add("TenantId", "1");
        karachiClient.DefaultRequestHeaders.Add("X-Tenant-Slug", Nihon);

        var forged = await GetEmailsAsync(karachiClient, "?tenantId=1&TenantId=1");

        Assert.Equal(baseline.OrderBy(x => x), forged.OrderBy(x => x));
    }

    /// <summary>
    /// The case a read filter does not cover on its own.
    /// </summary>
    /// <remarks>
    /// The Role filter admits TenantId == null so that system roles are visible to everyone.
    /// Visibility is not mutability: without an explicit check, that same filter would let
    /// any tenant delete a shared row. This is the write-side half of criterion C4, and the
    /// pattern the Phase 0.5 global vehicle catalog will depend on.
    /// </remarks>
    [Fact]
    public async Task Tenant_cannot_write_to_a_globally_visible_row()
    {
        var client = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");

        var roles = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/roles");
        Assert.NotNull(roles);

        var systemRole = roles!.First(r => r.GetProperty("isSystemRole").GetBoolean());

        // A GUID since D17: /roles/{id} is a top-level route, so the id it hands out and takes
        // back is the external one.
        var id = systemRole.GetProperty("id").GetGuid();

        var response = await client.DeleteAsync($"/api/v1/roles/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var stillThere = await db.Roles.IgnoreQueryFilters().AnyAsync(r => r.PublicId == id);
        Assert.True(stillThere, "A system role must survive a tenant's delete attempt.");
    }

    [Fact]
    public async Task Tenant_cannot_update_another_tenants_records()
    {
        var karachiClient = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        // Find a role owned by Nihon, using an unfiltered context so the test can see what
        // the API must not.
        long nihonRoleId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var nihonTenantId = await db.Tenants
                .Where(t => t.Slug == Nihon)
                .Select(t => t.Id)
                .FirstAsync();

            var role = new Role
            {
                TenantId = nihonTenantId,
                Name = "Nihon Private Role " + Guid.NewGuid().ToString("N")[..6],
            };

            db.Roles.Add(role);
            await db.SaveChangesAsync();
            nihonRoleId = role.Id;
        }

        var response = await karachiClient.DeleteAsync($"/api/v1/roles/{nihonRoleId}");

        // 404, not 403: to Karachi the row does not exist. Answering 403 would confirm that
        // some other tenant owns a role with this id.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
            Assert.True(await db.Roles.IgnoreQueryFilters().AnyAsync(r => r.Id == nihonRoleId));
        }
    }

    [Fact]
    public async Task Dual_membership_user_sees_only_the_active_tenant()
    {
        var inNihon = await _factory.AuthenticatedClientAsync("multi@example.test", Nihon);
        var inKarachi = await _factory.AuthenticatedClientAsync("multi@example.test", Karachi);

        var nihonTenant = await inNihon.GetFromJsonAsync<JsonElement>("/api/v1/tenants/current");
        Assert.Equal(Nihon, nihonTenant.GetProperty("slug").GetString());

        // The same identity holds ReadOnly in Karachi, so tenants/current is permitted but
        // the member list is not. Permissions resolve per tenant (criterion E6).
        var karachiTenant = await inKarachi.GetFromJsonAsync<JsonElement>("/api/v1/tenants/current");
        Assert.Equal(Karachi, karachiTenant.GetProperty("slug").GetString());

        var members = await inKarachi.GetAsync("/api/v1/tenants/current/members");
        Assert.Equal(HttpStatusCode.Forbidden, members.StatusCode);
    }

    [Fact]
    public async Task Suspension_in_one_tenant_does_not_affect_another()
    {
        var client = _factory.CreateApiClient();

        var allowed = await _factory.LoginAsync(client, "suspended@example.test", Nihon);
        Assert.Equal(200, allowed.StatusCode);
        Assert.NotNull(allowed.AccessToken);

        var refused = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "suspended@example.test",
            password = ApiFactory.SeedPassword,
            tenantSlug = Karachi,
        });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Unresolved_tenant_context_matches_no_rows()
    {
        // Fail closed: TenantIdOrZero is 0 when no tenant is resolved, and 0 is never a valid
        // tenant id. The alternative - an unresolved context matching everything - is the
        // failure mode this design exists to prevent.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        Assert.Empty(await db.TenantUsers.ToListAsync());
        Assert.Empty(await db.UserRoles.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());

        // Unfiltered, the same tables are populated - proving the emptiness above is the
        // filter working, not an empty database.
        Assert.NotEmpty(await db.TenantUsers.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task System_roles_are_visible_to_every_tenant()
    {
        var nihon = await _factory.AuthenticatedClientAsync("owner@nihon-motors.test");
        var karachi = await _factory.AuthenticatedClientAsync("owner@karachi-auto.test");

        var nihonRoles = await nihon.GetFromJsonAsync<JsonElement[]>("/api/v1/roles");
        var karachiRoles = await karachi.GetFromJsonAsync<JsonElement[]>("/api/v1/roles");

        var nihonSystem = nihonRoles!
            .Where(r => r.GetProperty("isSystemRole").GetBoolean())
            .Select(r => r.GetProperty("name").GetString())
            .OrderBy(n => n);

        var karachiSystem = karachiRoles!
            .Where(r => r.GetProperty("isSystemRole").GetBoolean())
            .Select(r => r.GetProperty("name").GetString())
            .OrderBy(n => n);

        Assert.Equal(SystemRoles.All.OrderBy(n => n), nihonSystem);
        Assert.Equal(nihonSystem, karachiSystem);
    }

    private static async Task<List<string>> GetEmailsAsync(HttpClient client, string query = "")
    {
        var members = await client.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/tenants/current/members" + query);

        return members!.Select(m => m.GetProperty("email").GetString()!).ToList();
    }
}
