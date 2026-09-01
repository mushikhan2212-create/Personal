using System.Net;
using CarDealer.Application.VehicleSources;
using CarDealer.Integrations.Carapis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CarDealer.UnitTests;

/// <summary>
/// Transport behaviour of the Carapis adapter: paging, retry, and the quota guard.
/// </summary>
/// <remarks>
/// A stub handler rather than a live call - the tests must pass in CI, where the provider is
/// unreachable and where hammering a third party to prove backoff would be rude besides. A
/// FakeTimeProvider means the backoff delays are asserted rather than waited out.
/// </remarks>
public class CarapisVehicleProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public StubHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            ApiKeys.Add(request.Headers.TryGetValues("X-API-Key", out var v) ? string.Join(",", v) : null);

            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }

        public List<string?> ApiKeys { get; } = [];
    }

    private static HttpResponseMessage Ok(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static (CarapisVehicleProvider Provider, StubHandler Handler, FakeTimeProvider Time) Build(
        StubHandler handler, CarapisOptions? options = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.carapis.com") };

        var provider = new CarapisVehicleProvider(
            http,
            Options.Create(options ?? new CarapisOptions { ApiKey = "test-key" }),
            NullLogger<CarapisVehicleProvider>.Instance,
            time);

        return (provider, handler, time);
    }

    /// <summary>
    /// Pumps virtual time until the operation finishes.
    /// </summary>
    /// <remarks>
    /// The retry loop registers a fresh timer after every failure, and FakeTimeProvider.Advance
    /// only releases timers that already exist - so a single advance completes the first
    /// backoff and then waits forever on the second. The brief real yield gives the
    /// continuation a chance to register the next one. Bounded by a real timeout so a genuine
    /// deadlock fails the test rather than hanging the suite.
    /// </remarks>
    private static async Task<T> DrainAsync<T>(Task<T> task, FakeTimeProvider time)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.WhenAny(task, Task.Delay(5));
        }

        Assert.True(task.IsCompleted, "Operation did not complete within the virtual-time budget.");
        return await task;
    }

    private const string OnePage = """
        {"count":1722,"page":1,"pages":18,"page_size":100,"has_next":true,
         "results":[{"id":"e35cbca7-ab36-4720-a171-627690b25da7","source_code":"sbtjapan"}]}
        """;

    [Fact]
    public async Task A_page_request_carries_the_source_filter_and_the_api_key()
    {
        var handler = new StubHandler(Ok(OnePage));
        var (provider, _, _) = Build(handler);

        await provider.FetchPageAsync(new VehicleSourceQuery { SourceCode = "sbtjapan" });

        Assert.Contains("source=sbtjapan", handler.Requests[0]);
        Assert.Contains("/apix/catalog_api/vehicles/", handler.Requests[0]);
        Assert.Equal("test-key", handler.ApiKeys[0]);
    }

    [Fact]
    public async Task Range_filters_use_the_min_and_max_prefix()
    {
        var handler = new StubHandler(Ok(OnePage));
        var (provider, _, _) = Build(handler);

        await provider.FetchPageAsync(new VehicleSourceQuery
        {
            SourceCode = "sbtjapan", MinYear = 2015, MaxYear = 2020, MaxMileage = 80_000,
        });

        // The API rejects an unrecognised parameter with a 400 rather than ignoring it, so a
        // _min suffix here would fail every request rather than degrade quietly.
        Assert.Contains("min_year=2015", handler.Requests[0]);
        Assert.Contains("max_year=2020", handler.Requests[0]);
        Assert.Contains("max_mileage=80000", handler.Requests[0]);
    }

    [Fact]
    public async Task A_source_outside_the_permitted_set_is_refused_before_any_request()
    {
        var handler = new StubHandler(Ok(OnePage));
        var (provider, _, _) = Build(handler);

        // Decision D12 restricts the POC to Japanese exporters. Reaching for a Polish
        // classifieds site is a configuration error, not a query.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.FetchPageAsync(new VehicleSourceQuery { SourceCode = "olx_pl" }));

        Assert.Contains("D12", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Paging_metadata_is_carried_through_so_the_caller_can_bound_the_run()
    {
        var handler = new StubHandler(Ok(OnePage));
        var (provider, _, _) = Build(handler);

        var page = await provider.FetchPageAsync(new VehicleSourceQuery { SourceCode = "sbtjapan" });

        Assert.Equal(1722, page.TotalCount);
        Assert.Equal(18, page.TotalPages);
        Assert.True(page.HasNextPage);
        Assert.Single(page.Records);
    }

    [Fact]
    public async Task The_raw_payload_of_each_record_is_preserved()
    {
        var handler = new StubHandler(Ok(OnePage));
        var (provider, _, _) = Build(handler);

        var page = await provider.FetchPageAsync(new VehicleSourceQuery { SourceCode = "sbtjapan" });

        // SQL schema spec section 8: source records are kept so normalization can be re-run
        // without re-fetching.
        Assert.Contains("e35cbca7-ab36-4720-a171-627690b25da7", page.Records[0].RawPayload);
        Assert.Equal("sbtjapan", page.Records[0].SourceCode);
    }

    [Fact]
    public async Task A_429_is_retried_after_backing_off()
    {
        var handler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            Ok(OnePage));

        var (provider, _, time) = Build(handler);

        var page = await DrainAsync(
            provider.FetchPageAsync(new VehicleSourceQuery { SourceCode = "sbtjapan" }), time);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1722, page.TotalCount);
    }

    [Fact]
    public async Task A_retry_after_header_is_honoured_in_preference_to_the_backoff_schedule()
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(30));

        var handler = new StubHandler(throttled, Ok(OnePage));
        var (provider, _, time) = Build(handler);

        var task = provider.FetchPageAsync(new VehicleSourceQuery { SourceCode = "sbtjapan" });

        // The exponential schedule would have retried after 2s; the server said 30, and the
        // server wins. Give the first attempt room to fail and register its timer first.
        await Task.WhenAny(task, Task.Delay(50));
        time.Advance(TimeSpan.FromSeconds(2));
        await Task.WhenAny(task, Task.Delay(50));

        Assert.False(task.IsCompleted);
        Assert.Single(handler.Requests);

        await DrainAsync(task, time);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_400_is_not_retried()
    {
        // A misspelled parameter is a 400 however many times it is sent, and the API returns
        // the valid parameter list with it. Retrying wastes quota and delays the real error.
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"detail":"Unknown query parameter 'year_min'."}"""),
        });

        var (provider, _, _) = Build(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.FetchPageAsync(new VehicleSourceQuery { SourceCode = "sbtjapan" }));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Retries_are_bounded_and_then_the_failure_surfaces()
    {
        var handler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var (provider, _, time) = Build(handler, new CarapisOptions { ApiKey = "k", MaxRetries = 2 });

        var task = provider.FetchPageAsync(new VehicleSourceQuery { SourceCode = "sbtjapan" });

        await Assert.ThrowsAsync<HttpRequestException>(() => DrainAsync(task, time));
        Assert.Equal(3, handler.Requests.Count); // initial attempt plus two retries
    }

    [Fact]
    public async Task A_missing_vehicle_is_null_rather_than_an_error()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var (provider, _, _) = Build(handler);

        Assert.Null(await provider.FetchDetailAsync("does-not-exist"));
    }

    [Fact]
    public async Task Sources_are_listed_with_their_availability_recorded_unmapped()
    {
        var handler = new StubHandler(Ok("""
            [{"code":"sbtjapan","name":"Sbtjapan","region":"other","country":"","availability":"on_demand","last_parsed_at":null},
             {"code":"goonet_exchange","name":"Goo-net Exchange","region":"japan","country":"Japan","availability":"live","last_parsed_at":null}]
            """));

        var (provider, _, _) = Build(handler);
        var sources = await provider.ListSourcesAsync();

        Assert.Equal(2, sources.Count);

        // availability is recorded as the provider's own word, not interpreted: it describes
        // provisioning, and does not predict whether a source carries data.
        Assert.Equal("on_demand", sources[0].Availability);
        Assert.Null(sources[0].Country); // blank country becomes null
        Assert.Equal("live", sources[1].Availability);
    }
}
