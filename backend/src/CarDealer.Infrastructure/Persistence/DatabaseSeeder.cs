using CarDealer.Application.Abstractions;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.Persistence;

/// <summary>
/// Deterministic seed data (SQL schema spec section 12).
/// </summary>
/// <remarks>
/// Under decision D10 Phase 0 ships no UI, so this fixture is the only way to exercise
/// multi-tenancy by hand through Swagger. That makes it a deliverable in its own right
/// rather than a convenience - see docs/spec/06-phase-0-acceptance.md section S.
///
/// Idempotent: re-running must not duplicate rows (criterion B5).
/// </remarks>
public sealed class DatabaseSeeder
{
    // Fixed GUIDs keep the seed deterministic across runs and machines.
    private static readonly Guid NihonPublicId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid KarachiPublicId = new("22222222-2222-2222-2222-222222222222");

    public const string NihonSlug = "nihon-motors";
    public const string KarachiSlug = "karachi-auto";

    private readonly CarDealerDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        CarDealerDbContext db,
        IPasswordHasher passwordHasher,
        IDateTimeProvider clock,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Seeds reference data and, when <paramref name="includeDevelopmentUsers"/> is true, the
    /// local test fixture.
    /// </summary>
    /// <remarks>
    /// Development users must never be created in Production (criterion S). The caller
    /// passes false there; the guard lives at the call site in Program.cs so that this class
    /// stays usable from tests.
    /// </remarks>
    public async Task SeedAsync(bool includeDevelopmentUsers, CancellationToken ct = default)
    {
        await SeedPermissionsAsync(ct).ConfigureAwait(false);
        await SeedSystemRolesAsync(ct).ConfigureAwait(false);
        await SeedVehicleSourcesAsync(ct).ConfigureAwait(false);
        await SeedExchangeRatesAsync(ct).ConfigureAwait(false);

        if (includeDevelopmentUsers)
        {
            await SeedDevelopmentFixtureAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Registers the shared sources the POC syncs (decision D12).
    /// </summary>
    /// <remarks>
    /// Reference data rather than a development fixture, so it is seeded in every environment:
    /// these rows are what a sync targets, and a source that is not registered cannot be
    /// synced at all.
    ///
    /// The Carapis codes are the ones proven to return data by a count call, which is not a
    /// formality - six sources appear under two or three codes disagreeing about themselves,
    /// and `sbt_japan` returns nothing where `sbtjapan` returns 1,722. Shared, so their
    /// vehicles land in the global catalog with a null TenantId.
    ///
    /// ProviderType is not a label: the sync pipeline resolves its normalizer from it, so a
    /// source registered with the wrong type cannot read its own payloads.
    /// </remarks>
    private async Task SeedVehicleSourcesAsync(CancellationToken ct)
    {
        (string Code, string Name, string BaseUrl, VehicleSourceProviderType Provider, VehicleSourceType Kind)[] sources =
        [
            ("sbtjapan", "SBT Japan", "https://www.sbtjapan.com",
                VehicleSourceProviderType.Carapis, VehicleSourceType.Api),
            ("goonet_exchange", "Goo-net Exchange", "https://www.goo-net-exchange.com",
                VehicleSourceProviderType.Carapis, VehicleSourceType.Api),

            // A source the JSON import path can actually target. Without one, every code in
            // the documented import URL belongs to a Carapis source, whose adapter cannot read
            // the import format - so the first import would fail every record and blame the
            // file. Registered here so the commands in the README work against a fresh
            // database with no setup step first (decision D13).
            ("file-import", "File import", null!,
                VehicleSourceProviderType.DealerJson, VehicleSourceType.File),
        ];

        foreach (var (code, name, baseUrl, provider, kind) in sources)
        {
            var existing = await _db.VehicleSources
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Code == code, ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                continue;
            }

            _db.VehicleSources.Add(new VehicleSource
            {
                TenantId = null,
                Name = name,
                Code = code,
                ProviderType = provider,
                SourceType = kind,
                BaseUrl = baseUrl,
                IsShared = true,
                IsActive = true,
            });
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds a starting FX rate per currency the catalog quotes in (decision D6).
    /// </summary>
    /// <remarks>
    /// Reference data, not a development fixture: without a rate for a currency, every listing
    /// priced in it has a null base price, which excludes it from price range filters and puts
    /// it at the end of every price sort. A catalog that cannot be filtered by budget is not
    /// much of a catalog.
    ///
    /// Rates are append-only and quoted as units per USD. These are indicative starting values
    /// with an explicit Source of "seed" so nobody mistakes them for a market feed - replacing
    /// them means inserting newer rows, never editing these, because listings pin the row they
    /// used and rewriting it would retroactively change historical prices.
    /// </remarks>
    /// <summary>
    /// Two sources matching the sample import files, so the documented walkthrough works.
    /// </summary>
    /// <remarks>
    /// Development only, and deliberately: these are demonstration sources, not reference data
    /// any real deployment needs.
    ///
    /// The codes must match the `sourceCode` inside `docs/spec/examples/import-exporter-*.json`
    /// exactly. The import endpoint refuses a document that names a different source, which is
    /// the right guard - importing one exporter's stock under another's misattributes every
    /// car - but it also means a sample file is unusable until the source it names exists.
    ///
    /// Two of them rather than one, because the same cars appear in both files: importing
    /// them in turn is what demonstrates cross-source deduplication, and a single source
    /// would just look like a re-import.
    /// </remarks>
    private async Task SeedSampleImportSourcesAsync(CancellationToken ct)
    {
        (string Code, string Name)[] samples =
        [
            ("exporter-a", "Exporter A (sample data)"),
            ("exporter-b", "Exporter B (sample data)"),
        ];

        foreach (var (code, name) in samples)
        {
            var exists = await _db.VehicleSources
                .IgnoreQueryFilters()
                .AnyAsync(s => s.Code == code, ct)
                .ConfigureAwait(false);

            if (exists)
            {
                continue;
            }

            _db.VehicleSources.Add(new VehicleSource
            {
                TenantId = null,
                Name = name,
                Code = code,
                ProviderType = VehicleSourceProviderType.DealerJson,
                SourceType = VehicleSourceType.File,
                IsShared = true,
                IsActive = true,
            });
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SeedExchangeRatesAsync(CancellationToken ct)
    {
        (string Quote, decimal Rate)[] rates =
        [
            ("JPY", 150.00m),
            ("KRW", 1_380.00m),
            ("EUR", 0.92m),
            ("GBP", 0.79m),
            ("AED", 3.67m),
            ("PKR", 278.00m),
            ("KES", 129.00m),
        ];

        foreach (var (quote, rate) in rates)
        {
            var exists = await _db.ExchangeRates
                .AnyAsync(r => r.BaseCurrencyCode == "USD" && r.QuoteCurrencyCode == quote, ct)
                .ConfigureAwait(false);

            if (exists)
            {
                continue;
            }

            _db.ExchangeRates.Add(new ExchangeRate
            {
                BaseCurrencyCode = "USD",
                QuoteCurrencyCode = quote,
                Rate = rate,
                AsOfUtc = _clock.UtcNow,
                Source = "seed",
            });
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SeedPermissionsAsync(CancellationToken ct)
    {
        var existing = await _db.Permissions
            .Select(p => p.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var missing = Permissions.All
            .Where(kvp => !existing.Contains(kvp.Key))
            .Select(kvp => new Permission { Code = kvp.Key, Description = kvp.Value })
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        _db.Permissions.AddRange(missing);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Seeded {PermissionCount} permissions.", missing.Count);
    }

    private async Task SeedSystemRolesAsync(CancellationToken ct)
    {
        // System roles have TenantId null, so the Role query filter admits them for every
        // tenant. IgnoreQueryFilters keeps the seeder correct even with no tenant resolved.
        var existingRoles = await _db.Roles
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var roleName in SystemRoles.All)
        {
            if (existingRoles.All(r => r.Name != roleName))
            {
                _db.Roles.Add(new Role
                {
                    TenantId = null,
                    Name = roleName,
                    Description = $"System role: {roleName}",
                });
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var roles = await _db.Roles
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var permissions = await _db.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id, ct)
            .ConfigureAwait(false);

        var existingGrants = await _db.RolePermissions
            .Select(rp => new { rp.RoleId, rp.PermissionId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var grantSet = existingGrants.Select(g => (g.RoleId, g.PermissionId)).ToHashSet();
        var added = 0;

        foreach (var (roleName, codes) in Permissions.SystemRoleGrants)
        {
            var role = roles.FirstOrDefault(r => r.Name == roleName);

            if (role is null)
            {
                continue;
            }

            foreach (var code in codes)
            {
                if (!permissions.TryGetValue(code, out var permissionId))
                {
                    continue;
                }

                if (grantSet.Add((role.Id, permissionId)))
                {
                    _db.RolePermissions.Add(new RolePermission
                    {
                        RoleId = role.Id,
                        PermissionId = permissionId,
                    });
                    added++;
                }
            }
        }

        if (added > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Seeded {GrantCount} role-permission grants.", added);
        }
    }

    private async Task SeedDevelopmentFixtureAsync(CancellationToken ct)
    {
        await SeedSampleImportSourcesAsync(ct).ConfigureAwait(false);

        var nihon = await EnsureTenantAsync(NihonPublicId, NihonSlug, "Nihon Motors", "JPY", "JP", ct)
            .ConfigureAwait(false);

        var karachi = await EnsureTenantAsync(
                KarachiPublicId, KarachiSlug, "Karachi Auto Imports", "USD", "PK", ct)
            .ConfigureAwait(false);

        var roles = await _db.Roles
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == null)
            .ToDictionaryAsync(r => r.Name, r => r.Id, ct)
            .ConfigureAwait(false);

        // The last two users are the point of this fixture. Without a user who belongs to
        // two tenants, and one suspended in only one of them, decision D2's multi-tenant
        // identity is untested (criteria C5, C8, E6).
        await EnsureUserAsync("owner@nihon-motors.test", "Aiko", "Tanaka",
            [(nihon, SystemRoles.TenantOwner, MembershipStatus.Active)], roles, ct).ConfigureAwait(false);

        await EnsureUserAsync("sales@nihon-motors.test", "Kenji", "Sato",
            [(nihon, SystemRoles.Salesperson, MembershipStatus.Active)], roles, ct).ConfigureAwait(false);

        await EnsureUserAsync("readonly@nihon-motors.test", "Mei", "Kobayashi",
            [(nihon, SystemRoles.ReadOnly, MembershipStatus.Active)], roles, ct).ConfigureAwait(false);

        await EnsureUserAsync("owner@karachi-auto.test", "Bilal", "Ahmed",
            [(karachi, SystemRoles.TenantOwner, MembershipStatus.Active)], roles, ct).ConfigureAwait(false);

        await EnsureUserAsync("multi@example.test", "Sara", "Khan",
            [
                (nihon, SystemRoles.Admin, MembershipStatus.Active),
                (karachi, SystemRoles.ReadOnly, MembershipStatus.Active),
            ], roles, ct).ConfigureAwait(false);

        await EnsureUserAsync("suspended@example.test", "Omar", "Farooq",
            [
                (nihon, SystemRoles.Salesperson, MembershipStatus.Active),
                (karachi, SystemRoles.Salesperson, MembershipStatus.Suspended),
            ], roles, ct).ConfigureAwait(false);
    }

    private async Task<Tenant> EnsureTenantAsync(
        Guid publicId, string slug, string name, string currency, string country, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct).ConfigureAwait(false);

        if (tenant is not null)
        {
            return tenant;
        }

        tenant = new Tenant
        {
            PublicId = publicId,
            Slug = slug,
            Name = name,
            Status = TenantStatus.Active,
            DefaultCurrencyCode = currency,
            DefaultCountryCode = country,
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Seeded tenant {TenantSlug}.", slug);

        return tenant;
    }

    private async Task EnsureUserAsync(
        string email,
        string firstName,
        string lastName,
        IReadOnlyList<(Tenant Tenant, string RoleName, MembershipStatus Status)> memberships,
        IReadOnlyDictionary<string, long> roles,
        CancellationToken ct)
    {
        var normalisedEmail = email.ToLowerInvariant();

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Email == normalisedEmail, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            user = new User
            {
                PublicId = Guid.NewGuid(),
                Email = normalisedEmail,
                FirstName = firstName,
                LastName = lastName,
                Status = UserStatus.Active,
                PasswordHash = _passwordHasher.Hash(DevelopmentPassword),
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        foreach (var (tenant, roleName, status) in memberships)
        {
            var membership = await _db.TenantUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.TenantId == tenant.Id && m.UserId == user.Id, ct)
                .ConfigureAwait(false);

            if (membership is null)
            {
                _db.TenantUsers.Add(new TenantUser
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    MembershipStatus = status,
                    JoinedAtUtc = status == MembershipStatus.Active ? _clock.UtcNow : null,
                });
            }

            if (roles.TryGetValue(roleName, out var roleId))
            {
                var hasRole = await _db.UserRoles
                    .IgnoreQueryFilters()
                    .AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == roleId && ur.TenantId == tenant.Id, ct)
                    .ConfigureAwait(false);

                if (!hasRole)
                {
                    _db.UserRoles.Add(new UserRole
                    {
                        UserId = user.Id,
                        RoleId = roleId,
                        TenantId = tenant.Id,
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Password for every seeded development account. Documented in the README (criterion K4)
    /// and only ever created outside Production.
    /// </summary>
    public const string DevelopmentPassword = "Dev_Passw0rd!";
}
