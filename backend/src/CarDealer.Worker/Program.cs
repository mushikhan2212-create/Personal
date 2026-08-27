using CarDealer.Infrastructure;
using CarDealer.Infrastructure.Jobs;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// Dedicated background job processor (master prompt section 5).
//
// The API also hosts a Hangfire server so that local development needs only one process.
// This project exists so job processing can be scaled and deployed independently of request
// handling - long imports, synchronisation and media work must not compete with API traffic.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(
        builder.Configuration.GetConnectionString("Default"),
        new SqlServerStorageOptions
        {
            // The API owns schema preparation. Two processes racing to create the same
            // Hangfire tables on startup is a needless failure mode.
            PrepareSchemaIfNecessary = false,
            QueuePollInterval = TimeSpan.FromSeconds(15),

            // Must match the API's value: whichever process fetches a job owns the
            // invisibility window, so leaving this at Hangfire's 30-minute default here
            // would strand work killed on the worker (criterion H5).
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        }));

builder.Services.AddHangfireServer();
builder.Services.AddScoped<EchoJob>();

var host = builder.Build();

await host.RunAsync().ConfigureAwait(false);
