using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using CarDealer.Api.Authorization;
using System.Text.Json.Serialization;
using CarDealer.Api.Serialization;
using CarDealer.Api.Configuration;
using CarDealer.Api.Middleware;
using CarDealer.Api.Services;
using CarDealer.Application.Abstractions;
using CarDealer.Infrastructure;
using CarDealer.Infrastructure.Auth;
using CarDealer.Infrastructure.Persistence;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Structured logging with tenant/user/correlation context (master prompt section 4,
// criteria G1, G2). LogContext is what carries the enriched properties pushed by middleware.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

builder.Services
    .AddControllers()

    // Every DateTime this API returns is a UTC field, but the ones read back from datetime2
    // arrive with Kind = Unspecified and would serialise without a Z, which a browser then
    // reads as local time. See UtcDateTimeConverter.
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());

        // Enums are accepted and emitted as names, never as numbers. Every response already
        // built its enum strings by hand, so this changes no output shape - what it fixes is
        // the input side, where a request body saying "DealerJson" was rejected and only the
        // magic number 5 was accepted. A contract that requires callers to know an enum's
        // ordinal also breaks silently the day someone reorders it.
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();

// One error contract for every failure, including the 401 and 403 produced by the
// authentication and authorization middleware rather than by a controller. Those never
// reach ApiResults.Problem, so without this they come back with an empty body and no
// correlation id - breaking criterion F4 exactly where a caller most needs the id.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["correlationId"] =
            CarDealer.Api.Common.ApiResults.CorrelationIdOf(context.HttpContext);
    });

// ---------------------------------------------------------------------------
// API versioning (criterion F1)
// ---------------------------------------------------------------------------
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });

// ---------------------------------------------------------------------------
// Authentication and authorization
// ---------------------------------------------------------------------------
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var signingKey = jwtSection["SigningKey"];

if (string.IsNullOrWhiteSpace(signingKey))
{
    throw new InvalidOperationException(
        "Jwt:SigningKey is not configured. Supply it through environment variables or a secret "
        + "store - it must never be committed (master prompt section 14).");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep standard JWT claim names as they were issued. With mapping on (the default),
        // "sub" and "email" are rewritten into legacy WS-Federation URIs, so every lookup by
        // JwtRegisteredClaimNames.Sub returns null - which surfaces as a spurious 401 rather
        // than as an obvious bug. Custom claims such as tenant_id are unaffected, which is
        // what makes the failure so easy to miss.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),

            // No leeway. The default five minutes would let a token criterion D8 expects to
            // be rejected keep working for another five minutes.
            ClockSkew = TimeSpan.Zero,
        };
    });

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// Rate limiting on authentication endpoints (master prompt section 14, criterion I2)
// ---------------------------------------------------------------------------
builder.Services.AddOptions<RateLimitOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var rateLimits = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>()
                 ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        // Partition by remote IP: a per-user key would let an attacker spray many usernames
        // from one host without ever tripping a limit.
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimits.PermitLimit,
            Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
            QueueLimit = 0,
        }));
});

// ---------------------------------------------------------------------------
// Health checks (criteria F5, F6)
// ---------------------------------------------------------------------------
var healthChecks = builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."),
        name: "sql-server",
        tags: ["ready"]);

var redisConnectionString = builder.Configuration.GetConnectionString("Redis");

if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    healthChecks.AddRedis(redisConnectionString, name: "redis", tags: ["ready"]);
}

// ---------------------------------------------------------------------------
// Background jobs (criteria H4, H5)
// ---------------------------------------------------------------------------
builder.Services.AddHangfire((sp, config) => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(
        builder.Configuration.GetConnectionString("Default"),
        new SqlServerStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(15),

            // Without this, a job whose worker dies mid-execution stays invisible for
            // Hangfire's 30-minute default before anyone retries it - so an API crash
            // strands in-flight work for half an hour (criterion H5). With a sliding
            // timeout the running worker heartbeats to keep the job invisible, so long
            // jobs are safe and only genuinely abandoned ones come back, after 5 minutes.
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        }));

builder.Services.AddHangfireServer();

// ---------------------------------------------------------------------------
// OpenAPI (criterion F7 - under decision D10 this IS the Phase 0 product surface)
// ---------------------------------------------------------------------------
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Car Dealer SaaS API",
        Version = "v1",
        Description =
            "Phase 0 foundation. Multi-tenancy, authentication, authorization, auditing.\n\n"
            + "Log in via /api/v1/auth/login, then use Authorize with the returned access token. "
            + "An access token is scoped to exactly one tenant; use /api/v1/auth/switch-tenant "
            + "to move between them.",
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token only - Swagger adds the 'Bearer ' prefix.",
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            },
            Array.Empty<string>()
        },
    });
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------

// Correlation first: everything downstream, including the exception handler, needs the id.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Gives empty-bodied status responses (notably 401 and 403 from the auth middleware) the
// same ProblemDetails shape as everything else.
app.UseStatusCodePages();

app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    // Criterion I4: HTTPS is enforced everywhere except local development.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Car Dealer SaaS API v1");
    options.DocumentTitle = "Car Dealer SaaS API";
});

app.UseRateLimiter();

app.UseAuthentication();

// After authentication: the tenant comes from the validated token and nowhere else.
app.UseMiddleware<TenantResolutionMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health");

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    // Liveness answers "is the process up?" and must not depend on SQL Server or Redis:
    // a dependency outage would otherwise get the container killed and restarted pointlessly.
    Predicate = _ => false,
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

await ApplyStartupTasksAsync(app).ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);

static async Task ApplyStartupTasksAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();

    var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);

    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

    // Reference data (permissions, system roles) is seeded everywhere. The development
    // fixture - real users with a known password - is created only outside Production.
    // Acceptance criterion S.
    var includeDevelopmentUsers = !app.Environment.IsProduction();

    await seeder.SeedAsync(includeDevelopmentUsers).ConfigureAwait(false);
}

/// <summary>
/// Exposed so the integration test host can reference the entry-point assembly.
/// </summary>
public partial class Program;
