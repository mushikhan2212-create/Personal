using CarDealer.Application.Abstractions;
using CarDealer.Application.Search;
using CarDealer.Application.VehicleSources;
using CarDealer.Infrastructure.Search;
using CarDealer.Infrastructure.Sources;
using CarDealer.Infrastructure.Sync;
using CarDealer.Integrations.Carapis;
using CarDealer.Integrations.FileImport;
using CarDealer.Application.Auth;
using CarDealer.Infrastructure.Audit;
using CarDealer.Infrastructure.Auth;
using CarDealer.Infrastructure.Caching;
using CarDealer.Infrastructure.Jobs;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Infrastructure.Pricing;
using CarDealer.Infrastructure.Services;
using CarDealer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CarDealer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddDbContext<CarDealerDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("Default"),
                sql => sql.EnableRetryOnFailure()));

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();

        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<DatabaseSeeder>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.Configure<LocalFileStorageOptions>(
            configuration.GetSection(LocalFileStorageOptions.SectionName));

        services.AddSingleton<IFileStorage, LocalFileStorage>();

        AddCaching(services, configuration, environment);

        services.AddScoped<IBackgroundJobScheduler, HangfireJobScheduler>();
        services.AddScoped<EchoJob>();

        // Search behind its abstraction (decision D4). Swapping in an engine later is a change
        // to this one line.
        services.AddScoped<ISearchProvider, SqlServerSearchProvider>();
        services.AddScoped<IExchangeRateService, ExchangeRateService>();

        AddVehicleSources(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the cache, enforcing that the in-memory fallback is development-only.
    /// </summary>
    /// <remarks>
    /// Acceptance criterion H2. Master prompt section 4 permits a "safe in-memory fallback
    /// only for development". Outside Development a missing Redis connection string is a
    /// startup failure, not a silent downgrade: an in-process cache in production looks
    /// healthy while losing every entry per instance, which is far worse than refusing to
    /// boot.
    /// </remarks>
    /// <summary>
    /// Registers vehicle source providers and the sync service.
    /// </summary>
    /// <remarks>
    /// Master prompt section 8 requires that Carapis can be disabled without breaking the rest
    /// of the platform, and this is where that is true or not. With no API key configured the
    /// provider is simply not registered: the catalog, the search and every other endpoint
    /// keep working, and only the sync trigger has nothing to run. Nothing above this method
    /// names Carapis.
    /// </remarks>
    private static void AddVehicleSources(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CarapisOptions>()
            .Bind(configuration.GetSection(CarapisOptions.SectionName))
            .ValidateDataAnnotations();

        var apiKey = configuration[$"{CarapisOptions.SectionName}:ApiKey"];

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var baseUrl = configuration[$"{CarapisOptions.SectionName}:BaseUrl"] ?? "https://api.carapis.com";

            services.AddHttpClient<CarapisVehicleProvider>(client =>
            {
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            });

            services.AddScoped<IVehicleSourceSyncProvider>(sp => sp.GetRequiredService<CarapisVehicleProvider>());
            services.AddScoped<IVehicleSourceDetailProvider>(sp => sp.GetRequiredService<CarapisVehicleProvider>());
            services.AddScoped<IVehicleSourceCatalogProvider>(sp => sp.GetRequiredService<CarapisVehicleProvider>());
        }

        // Normalizers are registered unconditionally and keyed by provider type inside the
        // sync service. Unlike a provider, a normalizer needs no credentials and no network -
        // it only reads payloads - so there is nothing to configure and no reason to withhold
        // one. The import path in particular must work with no API key set at all.
        services.AddScoped<CarapisNormalizer>();
        services.AddScoped<IVehicleRecordNormalizer>(sp => sp.GetRequiredService<CarapisNormalizer>());
        services.AddScoped<IVehicleRecordNormalizer, ImportNormalizer>();

        services.AddScoped<VehicleSyncService>();
        services.AddScoped<VehicleSourceRemovalService>();
    }

    private static void AddCaching(
        IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var redisConnection = configuration.GetConnectionString("Redis");

        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
                options.InstanceName = "cardealer:";
            });

            services.AddScoped<ICacheService, DistributedCacheService>();
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "No Redis connection string is configured. The in-memory cache fallback is "
                + "permitted only in Development (master prompt section 4). Set "
                + "ConnectionStrings__Redis for this environment.");
        }

        services.AddMemoryCache();
        services.AddScoped<ICacheService, InMemoryCacheService>();
    }
}
