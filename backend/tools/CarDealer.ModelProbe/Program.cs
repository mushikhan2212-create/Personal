using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CarDealer.Application.AI;
using CarDealer.Integrations.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// Which model should this dealer point the ranking at?
//
// Not a question to answer from a datasheet. The only thing that matters is whether a model can
// do THIS task - return schema-valid JSON that survives RankingGuards on a real candidate set -
// and that is measurable in about a minute per model.
//
// It deliberately uses the application's own RankingPrompt and RankingGuards rather than a copy.
// A probe that tested a paraphrase of the prompt would answer a question nobody asked.
//
//   ANTHROPIC-STYLE:  dotnet run -- --provider anthropic --model <id>
//   GROQ / OPENAI:    dotnet run -- [--model <id>] [--model <id>] ...
//   RATE-LIMITED:     dotnet run -- --max-tokens 1000
//
// With no --model, it asks the provider what it serves and probes everything plausible.

var provider = ArgValue("--provider") ?? "groq";
var baseUrl = ArgValue("--base-url");
var explicitModels = ArgValues("--model");

// Sized for the probe's own five-car set at roughly 190 output tokens per car, not for the
// application's default. It matters: a rate-limited account rejects a request on the ceiling it
// ASKS for rather than what it uses, so probing at 8,000 made two models 429 that would have
// answered comfortably - excluding them for a setting rather than for a shortcoming.
var maxTokens = int.TryParse(ArgValue("--max-tokens"), out var parsed) ? parsed : 2_000;

var apiKey = Environment.GetEnvironmentVariable("AI__ApiKey")
    ?? Environment.GetEnvironmentVariable("GROQ_API_KEY")
    ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine(
        "No key found. Set AI__ApiKey (or GROQ_API_KEY / ANTHROPIC_API_KEY) and run again.");
    return 1;
}

var services = new ServiceCollection();
services.AddHttpClient("ai-ranking");
var httpFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

var models = explicitModels.Count > 0
    ? explicitModels
    : await DiscoverAsync(httpFactory, apiKey, baseUrl);

if (models.Count == 0)
{
    Console.Error.WriteLine("No models to probe. Pass --model <id> explicitly.");
    return 1;
}

// Flagged exactly as the application flags one, because the flag is part of the payload the
// model reads. Probing an unflagged request would measure a prompt nobody sends.
var request = FitBands.Flag(SampleRequest());

Console.WriteLine();
Console.WriteLine($"Probing {models.Count} model(s) against the real ranking task.");
Console.WriteLine(
    $"{request.Candidates.Count} candidate vehicles, {RankingPrompt.Version} prompt, "
    + $"max_tokens {maxTokens}.");
Console.WriteLine();

var results = new List<Result>();

foreach (var model in models)
{
    Console.Write($"  {model,-52} ");

    var options = Options.Create(new AIOptions
    {
        Provider = provider,
        Model = model,
        ApiKey = apiKey,
        BaseUrl = baseUrl,
        TimeoutSeconds = 60,
        MaxTokens = maxTokens,
    });

    IAIProvider ranker = provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
        ? new AnthropicRankingProvider(options)
        : new OpenAiCompatibleRankingProvider(options, httpFactory);

    var stopwatch = Stopwatch.StartNew();
    AIRankingResult answer;

    try
    {
        answer = await ranker.RankAsync(request);
    }
    catch (Exception ex)
    {
        answer = AIRankingResult.Failed(ex.Message, provider, model);
    }

    stopwatch.Stop();

    // The same verdict the application would reach. A model that answers but cannot survive
    // this is a model that will fall back to price order every single time in production.
    var verdict = !answer.Succeeded
        ? answer.Failure ?? "no answer"
        : RankingGuards.Reject(request, answer.Ranked!) ?? "OK";

    results.Add(new Result(model, verdict == "OK", verdict, stopwatch.ElapsedMilliseconds, answer));

    // Printed in full, deliberately. The first run of this truncated at 60 characters and hid
    // which vehicle a rejected figure belonged to - the one thing needed to tell a hallucination
    // apart from a guard that is too strict. A probe that withholds the diagnosis is a worse
    // tool than no probe.
    Console.WriteLine(verdict == "OK"
        ? $"OK    {stopwatch.ElapsedMilliseconds,6} ms"
        : "no");

    if (verdict != "OK")
    {
        Console.WriteLine($"        {verdict}");
    }
}

Console.WriteLine();
Console.WriteLine("Usable models, fastest first:");
Console.WriteLine();

var usable = results.Where(r => r.Ok).OrderBy(r => r.Ms).ToList();

if (usable.Count == 0)
{
    Console.WriteLine("  None. Every model either refused, timed out, or failed a guard.");
    Console.WriteLine("  The most common cause is a model that ignores response_format:json_schema");
    Console.WriteLine("  and answers in prose. Look for one the provider documents as supporting");
    Console.WriteLine("  structured outputs.");
}

foreach (var r in usable)
{
    var usage = r.Answer.Usage;

    Console.WriteLine(
        $"  {r.Model,-52} {r.Ms,6} ms   "
        + $"{usage?.InputTokens ?? 0,6} in / {usage?.OutputTokens ?? 0,5} out");

    // Printed because latency and token counts say nothing about whether the ordering is
    // sensible, and that is the half only a person in this trade can judge.
    //
    // Shown after FitBands, which is what a salesperson would actually see. The model's own
    // order is no longer the final word, and printing it as though it were would put this tool
    // back to measuring something the product does not do.
    foreach (var entry in FitBands.Apply(request.Candidates, r.Answer.Ranked!).Take(3))
    {
        var car = request.Candidates.First(c => c.Id == entry.Id);
        var band = car.CloseToTheirLimits == true ? " [near a limit]" : string.Empty;

        Console.WriteLine(
            $"        #{entry.Rank} {car.Year} {car.Model} {car.Mileage:N0} km{band} "
            + $"- {string.Join("; ", entry.Reasons)}");
    }

    Console.WriteLine();
}

Console.WriteLine("Read the orderings above before choosing. A model that passes every guard can");
Console.WriteLine("still rank badly, and that is the one thing no test can tell you.");
Console.WriteLine();
Console.WriteLine("Cars marked [near a limit] only just satisfy something the customer set, and are");
Console.WriteLine("placed after the ones that do not - by the application, not by the model. Judge");
Console.WriteLine("the model on the order within each group, and on whether its reasons say so.");
Console.WriteLine();

return 0;

// ---------------------------------------------------------------------------------------

static string? ArgValue(string name)
{
    var args = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(args, name);

    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static List<string> ArgValues(string name)
{
    var args = Environment.GetCommandLineArgs();
    var found = new List<string>();

    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            found.Add(args[i + 1]);
        }
    }

    return found;
}

/// <summary>Asks the provider what it serves, and drops what cannot possibly rank a car.</summary>
static async Task<List<string>> DiscoverAsync(
    IHttpClientFactory factory, string apiKey, string? baseUrl)
{
    var root = (baseUrl ?? "https://api.groq.com/openai/v1").TrimEnd('/');

    using var http = factory.CreateClient("ai-ranking");
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    try
    {
        using var response = await http.GetAsync($"{root}/models");

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(
                $"Could not list models ({(int)response.StatusCode}). Pass --model explicitly.");
            return [];
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var ids = json.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString() ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToList();

        // Speech, embedding and moderation models are on the same list and cannot do this job.
        // Filtered by name because asking each one costs a call and a minute.
        // Best-effort and known to be incomplete: the first real run put two speech models
        // ("orpheus") through the full probe before their own API rejected them. Cheap to
        // extend, and a model that slips through costs one call rather than a wrong answer.
        string[] notRankers =
        [
            "whisper", "tts", "embed", "guard", "moderation", "vision-ocr", "orpheus",
            "speech", "audio", "rerank",
        ];

        var candidates = ids
            .Where(id => !notRankers.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(id => id)
            .ToList();

        Console.WriteLine($"The account serves {ids.Count} models; {candidates.Count} could rank.");

        return candidates;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not list models: {ex.Message}");
        return [];
    }
}

/// <summary>
/// A candidate set shaped like the real catalogue.
/// </summary>
/// <remarks>
/// Deliberately awkward, because an easy set makes every model look good. The cheapest car has
/// the highest mileage, the newest is dearest, one is missing its colour entirely, and two are
/// close enough that the ordering is a judgement rather than a sort. That is what this
/// catalogue actually looks like.
/// </remarks>
static RankingRequest SampleRequest() => new()
{
    Requirement = new RequirementBrief
    {
        Make = "Toyota",
        Model = "Corolla",
        MinYear = 2015,
        MaxMileage = 120_000,
        Transmission = "CVT",
        MaxPrice = 8_000m,
        DestinationCountryCode = "PK",
    },
    Candidates =
    [
        new CandidateVehicle
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Make = "Toyota", Model = "Corolla Axio", Variant = "G",
            Year = 2017, Mileage = 100_796, MileageUnit = "km", Colour = "Silver",
            Fuel = "Petrol", Transmission = "CVT", Steering = "RHD",
            RetailPrice = 7_400m, RetailCurrency = "USD", OfferCount = 2,
        },
        new CandidateVehicle
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Make = "Toyota", Model = "Corolla Axio", Variant = "X",
            Year = 2016, Mileage = 111_187, MileageUnit = "km",
            Fuel = "Petrol", Transmission = "CVT", Steering = "RHD",
            RetailPrice = 6_100m, RetailCurrency = "USD",
        },
        new CandidateVehicle
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Make = "Toyota", Model = "Corolla Axio", Variant = "Hybrid G",
            Year = 2017, Mileage = 78_382, MileageUnit = "km", Colour = "White",
            Fuel = "Hybrid", Transmission = "CVT", Steering = "RHD",
            RetailPrice = 7_900m, RetailCurrency = "USD",
        },
        new CandidateVehicle
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Make = "Toyota", Model = "Corolla Axio", Variant = "G",
            Year = 2016, Mileage = 65_803, MileageUnit = "km", Colour = "Black",
            Fuel = "Petrol", Transmission = "CVT", Steering = "RHD",
            RetailPrice = 7_350m, RetailCurrency = "USD", OfferCount = 3,
        },
        new CandidateVehicle
        {
            Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Make = "Toyota", Model = "Corolla Fielder", Variant = "X",
            Year = 2015, Mileage = 118_400, MileageUnit = "km", Colour = "Silver",
            Fuel = "Petrol", Transmission = "CVT", Steering = "RHD",
            RetailPrice = 5_600m, RetailCurrency = "USD",
        },
    ],
};

internal sealed record Result(
    string Model, bool Ok, string Verdict, long Ms, AIRankingResult Answer);
