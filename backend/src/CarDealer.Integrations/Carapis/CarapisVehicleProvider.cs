using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Web;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarDealer.Integrations.Carapis;

/// <summary>
/// Carapis adapter. POC only, until the licensing gate closes
/// ([O2](../../../../docs/spec/05-open-items.md)).
/// </summary>
/// <remarks>
/// The only class in the solution that knows Carapis exists. Everything above it depends on
/// <see cref="IVehicleSourceSyncProvider"/>, so disabling this provider removes a
/// registration and breaks nothing else - which is master prompt section 8's requirement that
/// Carapis can be turned off without breaking the platform.
/// </remarks>
public sealed class CarapisVehicleProvider
    : IVehicleSourceSyncProvider, IVehicleSourceDetailProvider, IVehicleSourceCatalogProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly CarapisOptions _options;
    private readonly ILogger<CarapisVehicleProvider> _logger;
    private readonly TimeProvider _time;

    public CarapisVehicleProvider(
        HttpClient http,
        IOptions<CarapisOptions> options,
        ILogger<CarapisVehicleProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string SourceCode => "carapis";

    public VehicleSourceProviderType ProviderType => VehicleSourceProviderType.Carapis;

    public async Task<VehicleSourcePage> FetchPageAsync(
        VehicleSourceQuery query, CancellationToken ct = default)
    {
        // Decision D12: no unfiltered call, ever. An unrecognised code would also be a 400
        // from the API, but failing here names the actual problem.
        if (!_options.PermittedSourceCodes.Contains(query.SourceCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Source '{query.SourceCode}' is not in the permitted set "
                + $"[{string.Join(", ", _options.PermittedSourceCodes)}]. Decision D12 restricts the "
                + "POC to Japanese exporters, and every request must name one.");
        }

        var q = HttpUtility.ParseQueryString(string.Empty);
        q["source"] = query.SourceCode;
        q["page"] = query.Page.ToString();
        q["page_size"] = Math.Min(query.PageSize, _options.PageSize).ToString();
        q["available_only"] = query.AvailableOnly ? "true" : "false";

        // Range filters use a min_/max_ prefix, not a _min/_max suffix, and an unrecognised
        // parameter is a 400 rather than a silent ignore - so these names are load-bearing.
        if (query.Make is not null) q["brand"] = query.Make;
        if (query.Model is not null) q["model"] = query.Model;
        if (query.MinYear is not null) q["min_year"] = query.MinYear.Value.ToString();
        if (query.MaxYear is not null) q["max_year"] = query.MaxYear.Value.ToString();
        if (query.MaxMileage is not null) q["max_mileage"] = query.MaxMileage.Value.ToString();

        var stopwatch = Stopwatch.StartNew();
        var body = await GetRequiredAsync($"/apix/catalog_api/vehicles/?{q}", ct).ConfigureAwait(false);
        stopwatch.Stop();

        var page = JsonSerializer.Deserialize<CarapisPage<JsonElement>>(body, Json)
            ?? throw new InvalidOperationException("Carapis returned a page that could not be read.");

        var now = _time.GetUtcNow().UtcDateTime;

        var records = page.Results
            .Select(el => new RawVehicleRecord
            {
                // The list projection has no listing_id, so the Carapis UUID is the external
                // id on this path. The detail call supplies the source's own id.
                ExternalId = el.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                SourceCode = query.SourceCode,
                RawPayload = el.GetRawText(),
                RetrievedAtUtc = now,
            })
            .Where(r => r.ExternalId.Length > 0)
            .ToList();

        _logger.LogInformation(
            "Carapis page {Page}/{Pages} for {Source}: {Returned} of {Total} records in {ElapsedMs}ms.",
            page.Page, page.Pages, query.SourceCode, records.Count, page.Count, stopwatch.ElapsedMilliseconds);

        return new VehicleSourcePage
        {
            Records = records,
            TotalCount = page.Count,
            Page = page.Page,
            TotalPages = page.Pages,
            HasNextPage = page.HasNext,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>
    /// Fetches one vehicle in full.
    /// </summary>
    /// <remarks>
    /// Costs one request per vehicle, which is why it is a separate capability rather than
    /// folded into the sync: only the detail response carries <c>vin</c> and
    /// <c>price_original</c>, so a caller that wants working deduplication pays 101 requests
    /// for a page of 100, and that has to be a deliberate choice against a quota.
    /// </remarks>
    public async Task<RawVehicleRecord?> FetchDetailAsync(string externalId, CancellationToken ct = default)
    {
        var body = await GetWithRetryAsync($"/apix/catalog_api/vehicles/{Uri.EscapeDataString(externalId)}/", ct)
            .ConfigureAwait(false);

        if (body is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(body);

        return new RawVehicleRecord
        {
            ExternalId = externalId,
            SourceCode = doc.RootElement.TryGetProperty("source_code", out var s)
                ? s.GetString() ?? SourceCode
                : SourceCode,
            RawPayload = body,
            RetrievedAtUtc = _time.GetUtcNow().UtcDateTime,
        };
    }

    public async Task<IReadOnlyList<VehicleSourceDescriptor>> ListSourcesAsync(CancellationToken ct = default)
    {
        var body = await GetRequiredAsync("/apix/catalog_api/sources/", ct).ConfigureAwait(false);

        var sources = JsonSerializer.Deserialize<List<CarapisSource>>(body, Json) ?? [];

        return sources
            .Where(s => !string.IsNullOrWhiteSpace(s.Code))
            .Select(s => new VehicleSourceDescriptor
            {
                Code = s.Code!,
                Name = s.Name ?? s.Code!,
                Region = s.Region,
                Country = string.IsNullOrWhiteSpace(s.Country) ? null : s.Country,
                Availability = s.Availability,
                LastParsedAtUtc = s.LastParsedAt,
            })
            .ToList();
    }

    /// <summary>
    /// As <see cref="GetWithRetryAsync"/>, for endpoints where a 404 is not a valid answer.
    /// </summary>
    /// <remarks>
    /// The list and sources endpoints always exist; a 404 from either means the base URL or
    /// path is wrong, and returning an empty page would hide that as "no results".
    /// </remarks>
    private async Task<string> GetRequiredAsync(string path, CancellationToken ct)
        => await GetWithRetryAsync(path, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException(
               $"Carapis returned 404 for '{path}', which should always exist. Check BaseUrl - "
               + "paths are /apix/... and the base carries no /v2 segment.");

    /// <summary>
    /// Issues a GET, retrying transient failures with exponential backoff.
    /// </summary>
    /// <remarks>
    /// Carapis publishes no rate limit, window or retry header for the vehicles endpoint, and
    /// documents a 429 only on the export endpoints. The ceiling therefore gets discovered by
    /// hitting it, which is exactly why backoff is not optional here. A 429 with a Retry-After
    /// is honoured; without one it falls back to the same exponential schedule.
    ///
    /// 4xx other than 429 is not retried: a 400 from a misspelled parameter will be a 400
    /// however many times it is sent, and the API returns the valid parameter list with it.
    /// </remarks>
    private async Task<string?> GetWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-API-Key", _options.ApiKey);

            HttpResponseMessage response;

            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt <= _options.MaxRetries)
            {
                await BackoffAsync(attempt, null, ex.Message, ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500;

                if (!retryable || attempt > _options.MaxRetries)
                {
                    // The body carries the valid parameter list on a 400, which is the most
                    // useful thing in the failure - but it is the provider's text, so it is
                    // logged rather than surfaced to a caller.
                    var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    _logger.LogError(
                        "Carapis {Path} failed with {Status} after {Attempts} attempt(s): {Detail}",
                        path, (int)response.StatusCode, attempt, Truncate(detail));

                    throw new HttpRequestException(
                        $"Carapis request failed with {(int)response.StatusCode}.", null, response.StatusCode);
                }

                await BackoffAsync(attempt, response.Headers.RetryAfter?.Delta,
                    $"HTTP {(int)response.StatusCode}", ct).ConfigureAwait(false);
            }
        }
    }

    private async Task BackoffAsync(int attempt, TimeSpan? retryAfter, string reason, CancellationToken ct)
    {
        var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

        _logger.LogWarning(
            "Carapis attempt {Attempt} of {Max} failed ({Reason}); retrying in {Delay}.",
            attempt, _options.MaxRetries, reason, delay);

        await Task.Delay(delay, _time, ct).ConfigureAwait(false);
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500] + "...";
}
