using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.Duplicates;

/// <summary>
/// Runs the duplicate scan over the whole catalogue.
/// </summary>
/// <remarks>
/// One pass, not one per tenant, which is the opposite of <c>RequirementAlertJob</c> and for a
/// reason: an alert belongs to a customer and therefore to a tenant, whereas most of this
/// catalogue is the global rows decision D1 shares between all of them. Walking tenants would
/// scan those rows once per tenant and race itself into duplicate candidate rows.
///
/// <para>
/// Ownership is still respected - the scan groups on <c>TenantScope</c>, so a tenant's private
/// vehicle can only ever pair with another of its own - but that is enforced in the query
/// rather than by the scope this runs in.
/// </para>
/// </remarks>
public sealed class DuplicateScanJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DuplicateScanJob> _log;

    public DuplicateScanJob(IServiceScopeFactory scopes, ILogger<DuplicateScanJob> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();

            var service = scope.ServiceProvider.GetRequiredService<DuplicateScanService>();

            await service.ScanAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed and logged, like the alert scan: this is best-effort background work,
            // and an unhandled exception here would take the Hangfire worker down over a
            // suggestion nobody was waiting on.
            _log.LogError(ex, "Duplicate scan failed.");
        }
    }
}
