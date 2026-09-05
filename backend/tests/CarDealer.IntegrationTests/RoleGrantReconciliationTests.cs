using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The seeder reconciles system role grants: it adds what is missing and revokes what is no
/// longer granted.
/// </summary>
/// <remarks>
/// Revocation exists because an add-only seeder can never narrow a role. Editing
/// <see cref="Permissions.SystemRoleGrants"/> would change the source while every database that
/// had already been seeded kept the old, wider grant - the code would say Admin-only and the
/// running system would stay permissive.
///
/// The dangerous half is the scope. Tenant-defined roles are assembled from an arbitrary
/// permission set through RolesController and were never derived from that table, so
/// reconciling them against it would delete a tenant's own configuration without asking. That
/// is what the second test is for, and it is the one that matters.
/// </remarks>
public sealed class RoleGrantReconciliationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RoleGrantReconciliationTests(ApiFactory factory) => _factory = factory;

    private async Task ReseedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

        await seeder.SeedAsync(includeDevelopmentUsers: true);
    }

    [Fact]
    public async Task Grant_a_system_role_no_longer_holds_is_revoked()
    {
        long roleId;
        long permissionId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            // ReadOnly holding tenants.manage is the kind of grant this is meant to clean up:
            // real once, wrong now, and invisible until something revokes it.
            roleId = await db.Roles.IgnoreQueryFilters()
                .Where(r => r.TenantId == null && r.Name == SystemRoles.ReadOnly)
                .Select(r => r.Id)
                .SingleAsync();

            permissionId = await db.Permissions
                .Where(p => p.Code == Permissions.TenantsManage)
                .Select(p => p.Id)
                .SingleAsync();

            db.RolePermissions.Add(new RolePermission
            {
                RoleId = roleId,
                PermissionId = permissionId,
            });

            await db.SaveChangesAsync();
        }

        await ReseedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            Assert.False(await db.RolePermissions
                .AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == permissionId));

            // Still holds what it is supposed to: revocation must not empty the role.
            var kept = await db.Permissions
                .Where(p => p.Code == Permissions.VehiclesRead)
                .Select(p => p.Id)
                .SingleAsync();

            Assert.True(await db.RolePermissions
                .AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == kept));
        }
    }

    [Fact]
    public async Task Sales_manager_loses_vehicles_sync_on_an_already_seeded_database()
    {
        // The migration path for the change this test class exists to protect: a database
        // seeded by an earlier build already has the row, and startup has to remove it.
        long roleId;
        long syncId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            roleId = await db.Roles.IgnoreQueryFilters()
                .Where(r => r.TenantId == null && r.Name == SystemRoles.SalesManager)
                .Select(r => r.Id)
                .SingleAsync();

            syncId = await db.Permissions
                .Where(p => p.Code == Permissions.VehiclesSync)
                .Select(p => p.Id)
                .SingleAsync();

            db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = syncId });
            await db.SaveChangesAsync();
        }

        await ReseedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            Assert.False(await db.RolePermissions
                .AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == syncId));
        }
    }

    [Fact]
    public async Task A_tenant_defined_role_keeps_every_grant_it_was_given()
    {
        long roleId;
        long[] granted;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            var tenantId = await db.Tenants.IgnoreQueryFilters()
                .Where(t => t.Slug == "nihon-motors")
                .Select(t => t.Id)
                .SingleAsync();

            // Deliberately named after a system role and given a permission that system role no
            // longer holds. If reconciliation matched on name rather than on the system-role id
            // set, this is the row it would wrongly delete.
            granted = await db.Permissions
                .Where(p => p.Code == Permissions.VehiclesRead || p.Code == Permissions.VehiclesSync)
                .Select(p => p.Id)
                .ToArrayAsync();

            var role = new Role
            {
                TenantId = tenantId,
                Name = SystemRoles.SalesManager,
                Description = "Tenant's own role, deliberately sharing a system role name.",
            };

            foreach (var permissionId in granted)
            {
                role.RolePermissions.Add(new RolePermission { PermissionId = permissionId });
            }

            db.Roles.Add(role);
            await db.SaveChangesAsync();

            roleId = role.Id;
        }

        await ReseedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            // IgnoreQueryFilters is required, not incidental: RolePermission carries a filter
            // mirroring Role's (CarDealerDbContext.ApplyTenantQueryFilters), and this scope has
            // no tenant resolved, so a plain query returns only system-role grants. Without it
            // this test reads an empty set and reports a deletion that never happened.
            var surviving = await db.RolePermissions
                .IgnoreQueryFilters()
                .Where(rp => rp.RoleId == roleId)
                .Select(rp => rp.PermissionId)
                .ToArrayAsync();

            Assert.Equal(granted.OrderBy(id => id), surviving.OrderBy(id => id));
        }
    }
}
