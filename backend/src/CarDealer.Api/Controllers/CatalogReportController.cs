using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Reporting;
using Microsoft.AspNetCore.Mvc;

namespace CarDealer.Api.Controllers;

/// <summary>
/// The POC evaluation measurements (master prompt section 8).
/// </summary>
/// <remarks>
/// Section 8 requires the POC to report data completeness, freshness, response time,
/// duplicates and images. This computes them from the live catalogue rather than from numbers
/// typed into a document once, so the report in docs/spec/09-poc-evaluation.md can be
/// regenerated against whatever has been imported since.
///
/// Gated on <c>vehicles.sync</c>: it reports across every source in the catalogue, including
/// per-source data quality, which is operator information rather than something a salesperson
/// needs. Unlike the diagnostics controller it is registered in every environment - the point
/// of a measurement is that it can be taken where the data actually is.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/catalog-report")]
public sealed class CatalogReportController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CatalogReportController(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    /// <summary>Measures the catalogue as it currently stands.</summary>
    /// <remarks>
    /// Runs in a DI scope with no tenant resolved, for the same reason the sync and import
    /// paths do: the report covers the whole global catalogue, and a tenant-scoped context
    /// would silently measure only what that tenant can see - producing a report that looks
    /// complete and is wrong. Authorization has already run against the caller.
    ///
    /// The search timings run each query twice and report the second, so the number is the
    /// steady-state cost rather than the cost of compiling a plan.
    /// </remarks>
    [HttpGet]
    [HasPermission(Permissions.VehiclesSync)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<CatalogReportService>();

        return Ok(await reports.BuildAsync(ct).ConfigureAwait(false));
    }
}
