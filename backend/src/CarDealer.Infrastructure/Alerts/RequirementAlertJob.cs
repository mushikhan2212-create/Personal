using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.Alerts;

/// <summary>
/// Runs the alert scan for every tenant, one at a time.
/// </summary>
/// <remarks>
/// The scanner itself knows nothing about tenants beyond the one resolved in its scope - which
/// is the point. This walks the list and gives each its own scope, so tenant isolation is the
/// DbContext's query filters doing their usual job rather than a predicate this class has to
/// remember to write.
///
/// <para>
/// One tenant failing does not stop the others. A scan is best-effort background work: a
/// malformed requirement or a transient timeout for one dealer should not mean nobody else
/// gets told about their stock, and the alternative - an exception ending the run - fails
/// silently in exactly the way nobody notices for a week.
/// </para>
/// </remarks>
public sealed class RequirementAlertJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RequirementAlertJob> _log;

    public RequirementAlertJob(IServiceScopeFactory scopes, ILogger<RequirementAlertJob> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        long[] tenantIds;

        // A scope with no tenant resolved, purely to read the list of them. IgnoreQueryFilters
        // is correct here and nowhere else in this feature: Tenants is the one table whose rows
        // are not owned by a tenant.
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

            // Suspended tenants are skipped: nobody can sign in to read the alerts, and
            // raising them would mean a reactivated tenant's inbox opens full of stale stock.
            tenantIds = await db.Tenants
                .IgnoreQueryFilters()
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => t.Id)
                .ToArrayAsync(ct)
                .ConfigureAwait(false);
        }

        var raised = 0;

        foreach (var tenantId in tenantIds)
        {
            try
            {
                using var scope = _scopes.CreateScope();

                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

                var scanner = scope.ServiceProvider.GetRequiredService<RequirementAlertScanner>();
                var result = await scanner.ScanAsync(ct).ConfigureAwait(false);

                raised += result.AlertsRaised;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Alert scan failed for tenant {TenantId}.", tenantId);
            }
        }

        _log.LogInformation(
            "Requirement alert scan finished: {Tenants} tenant(s), {Alerts} alert(s) raised.",
            tenantIds.Length,
            raised);
    }
}
