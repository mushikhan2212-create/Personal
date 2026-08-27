using CarDealer.Application.Abstractions;
using CarDealer.Domain.Common;
using CarDealer.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Infrastructure.Persistence;

public class CarDealerDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;

    public CarDealerDbContext(
        DbContextOptions<CarDealerDbContext> options,
        ITenantContext tenantContext,
        IDateTimeProvider clock)
        : base(options)
    {
        _tenantContext = tenantContext;
        _clock = clock;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // --- Phase 0.5 catalog ---------------------------------------------------------------

    public DbSet<VehicleSource> VehicleSources => Set<VehicleSource>();

    public DbSet<VehicleSourceConfiguration> VehicleSourceConfigurations
        => Set<VehicleSourceConfiguration>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<VehicleListing> VehicleListings => Set<VehicleListing>();

    public DbSet<VehicleImage> VehicleImages => Set<VehicleImage>();

    public DbSet<VehicleListingImage> VehicleListingImages => Set<VehicleListingImage>();

    public DbSet<TenantVehicle> TenantVehicles => Set<TenantVehicle>();

    public DbSet<VehicleMatchCandidate> VehicleMatchCandidates => Set<VehicleMatchCandidate>();

    public DbSet<VehicleMergeHistory> VehicleMergeHistories => Set<VehicleMergeHistory>();

    public DbSet<Make> Makes => Set<Make>();

    public DbSet<Model> Models => Set<Model>();

    public DbSet<SourceMakeModelAlias> SourceMakeModelAliases => Set<SourceMakeModelAlias>();

    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();

    public DbSet<SyncJobItem> SyncJobItems => Set<SyncJobItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CarDealerDbContext).Assembly);

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Applies tenant isolation at the model level (SQL schema spec section 9).
    /// </summary>
    /// <remarks>
    /// Every filter compares against <see cref="ITenantContext.TenantIdOrZero"/>, which is
    /// zero when no tenant is resolved. Zero matches no tenant, so an unauthenticated or
    /// tenant-less request sees nothing rather than everything. Fail closed, not open.
    ///
    /// Entities NOT filtered here, each for a stated reason:
    ///   Tenant      - it is the tenant; access is controlled by membership at the service layer.
    ///   User        - a global identity by decision D2.
    ///   Permission  - global reference data, identical for every tenant.
    ///   RolePermission - reached only through Role, which is filtered.
    ///   RefreshToken - looked up by a cryptographically random hash during refresh, before
    ///                  any tenant is resolved. Filtering it would break the refresh flow;
    ///                  the token hash is itself the capability.
    /// </remarks>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantUser>()
            .HasQueryFilter(e => e.TenantId == _tenantContext.TenantIdOrZero);

        modelBuilder.Entity<UserRole>()
            .HasQueryFilter(e => e.TenantId == _tenantContext.TenantIdOrZero);

        modelBuilder.Entity<AuditLog>()
            .HasQueryFilter(e => e.TenantId == _tenantContext.TenantIdOrZero);

        // System roles (TenantId null) are visible to every tenant; tenant-defined roles
        // only to their owner. This mirrors decision D1's shape and is the same pattern the
        // vehicle catalog will use in Phase 0.5.
        modelBuilder.Entity<Role>()
            .HasQueryFilter(e => e.TenantId == null || e.TenantId == _tenantContext.TenantIdOrZero);

        // Must mirror the Role filter exactly. Without it, RolePermission is queryable
        // without any tenant predicate, so another tenant's custom role composition would be
        // readable even though the Role row itself is hidden - the grants leak the shape of
        // a role we are deliberately concealing.
        modelBuilder.Entity<RolePermission>()
            .HasQueryFilter(e =>
                e.Role.TenantId == null || e.Role.TenantId == _tenantContext.TenantIdOrZero);

        // --- Phase 0.5 catalog (decision D1) ---------------------------------------------
        //
        // Null TenantId means the global catalog and is readable by every tenant; non-null is
        // one tenant's private inventory. This is a weaker guard than flat equality, because
        // a read filter that permits null also permits UPDATE and DELETE against those same
        // global rows. GuardGlobalCatalogWrites below is what closes that gap
        // (docs/spec/04-schema-delta.md section 1.4, case 3).
        modelBuilder.Entity<Vehicle>()
            .HasQueryFilter(e => e.TenantId == null || e.TenantId == _tenantContext.TenantIdOrZero);

        modelBuilder.Entity<VehicleListing>()
            .HasQueryFilter(e => e.TenantId == null || e.TenantId == _tenantContext.TenantIdOrZero);

        modelBuilder.Entity<VehicleImage>()
            .HasQueryFilter(e => e.TenantId == null || e.TenantId == _tenantContext.TenantIdOrZero);

        // The overlay is strictly tenant-owned - it is the table that holds one tenant's
        // price and notes over a shared car, so it never admits the null-is-global rule.
        modelBuilder.Entity<TenantVehicle>()
            .HasQueryFilter(e => e.TenantId == _tenantContext.TenantIdOrZero);

        // Sources may be shared (SQL schema spec section 3); their configurations, which
        // carry the credential reference, never are.
        modelBuilder.Entity<VehicleSource>()
            .HasQueryFilter(e => e.TenantId == null || e.TenantId == _tenantContext.TenantIdOrZero);

        modelBuilder.Entity<VehicleSourceConfiguration>()
            .HasQueryFilter(e => e.TenantId == _tenantContext.TenantIdOrZero);
    }

    /// <summary>
    /// Refuses to create, modify or delete a global catalog row from a tenant-scoped request
    /// path (docs/spec/04-schema-delta.md section 1.4, case 3).
    /// </summary>
    /// <remarks>
    /// The query filter deliberately lets every tenant READ global rows - that is the point
    /// of decision D1. It does not stop them writing to those rows, and a read filter is
    /// often mistaken for a write guard. Without this, any tenant could edit the price of a
    /// car in the shared catalog and change it for everyone.
    ///
    /// The rule is simply: if a tenant is resolved, that tenant may only touch its own rows.
    /// Sync jobs populate the global catalog from a background context where no tenant is
    /// resolved, so TenantIdOrZero is zero and this guard does not apply to them. That is the
    /// intended and only route to a global write.
    /// </remarks>
    private void GuardGlobalCatalogWrites()
    {
        var tenantId = _tenantContext.TenantIdOrZero;

        if (tenantId == 0)
        {
            // No tenant resolved: a system or sync path. Global writes are its job.
            return;
        }

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not IOptionallyTenantScoped scoped)
            {
                continue;
            }

            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (scoped.TenantId is null)
            {
                throw new InvalidOperationException(
                    $"Tenant {tenantId} attempted to {entry.State.ToString().ToLowerInvariant()} a "
                    + $"global {entry.Entity.GetType().Name} row. The global catalog is written "
                    + "only by sync jobs, never from a tenant-scoped request path "
                    + "(decision D1; docs/spec/04-schema-delta.md section 1.4).");
            }

            if (scoped.TenantId != tenantId)
            {
                throw new InvalidOperationException(
                    $"Tenant {tenantId} attempted to {entry.State.ToString().ToLowerInvariant()} a "
                    + $"{entry.Entity.GetType().Name} row owned by tenant {scoped.TenantId}.");
            }
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardGlobalCatalogWrites();
        ApplyTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        GuardGlobalCatalogWrites();
        ApplyTimestamps();
        return base.SaveChanges();
    }

    private void ApplyTimestamps()
    {
        var now = _clock.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is Entity added && added.CreatedAtUtc == default)
                    {
                        added.CreatedAtUtc = now;
                    }

                    if (entry.Entity is AuditableEntity addedAuditable)
                    {
                        addedAuditable.UpdatedAtUtc = now;
                    }

                    if (entry.Entity is UserRole addedUserRole && addedUserRole.CreatedAtUtc == default)
                    {
                        addedUserRole.CreatedAtUtc = now;
                    }

                    if (entry.Entity is RolePermission addedRolePermission
                        && addedRolePermission.CreatedAtUtc == default)
                    {
                        addedRolePermission.CreatedAtUtc = now;
                    }

                    break;

                case EntityState.Modified:
                    if (entry.Entity is AuditableEntity modified)
                    {
                        modified.UpdatedAtUtc = now;
                    }

                    break;
            }
        }
    }
}
