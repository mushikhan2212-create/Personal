using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CarDealer.Application.AI;
using CarDealer.Application.Abstractions;
using CarDealer.Application.Formatting;
using CarDealer.Application.Search;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.AI;

/// <summary>What a caller gets back from asking for a ranking.</summary>
/// <param name="Entries">The cars, best first. Never empty when candidates existed.</param>
/// <param name="Source">Whether a model produced this order or the filter did.</param>
/// <param name="Notice">Why the model's answer was not used, when it was not.</param>
/// <param name="Reused">True when a stored answer to the same question was served.</param>
public sealed record RankingOutcome(
    IReadOnlyList<RankedEntry> Entries,
    RecommendationSource Source,
    string? Notice,
    bool Reused);

/// <param name="Hit">The car, in the shape the search screen already renders.</param>
public sealed record RankedEntry(VehicleSearchHit Hit, int Rank, decimal Score, string[] Reasons);

/// <summary>
/// Ranks the stock that fits a requirement, and survives the model not helping.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline master prompt section 12 specifies: deterministic hard filters produce the
/// candidates, the model reorders them, a person reviews the result. The filters are the same
/// ones the matches screen already runs, so a ranking can only ever be a permutation of what
/// the salesperson would have seen anyway.
/// </para>
///
/// <para>
/// Every failure has the same answer - show the deterministic order and say why - so no path
/// through this class leaves the caller without a list. A missing key, a timeout, a refusal,
/// malformed JSON, a guard firing: all of them degrade to exactly what the product did before
/// this feature existed. That is the property that makes it safe to ship against a provider
/// nobody has measured yet.
/// </para>
/// </remarks>
public sealed class RecommendationService
{
    /// <summary>
    /// How many cars go to the model.
    /// </summary>
    /// <remarks>
    /// Section 11's "hard-filter candidates first; do not send millions of vehicles to an LLM",
    /// made a number. Twenty is a screenful - past that a salesperson is not reading the
    /// ordering anyway, and every extra car is tokens spent on a row nobody looks at. The
    /// candidates are drawn cheapest-first, so the window always contains the cars a budget
    /// makes most likely to sell.
    /// </remarks>
    public const int MaxCandidates = 20;

    private readonly CarDealerDbContext _db;
    private readonly ISearchProvider _search;
    private readonly IAIProvider _ai;
    private readonly ITenantContext _tenant;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<RecommendationService> _logger;

    public RecommendationService(
        CarDealerDbContext db,
        ISearchProvider search,
        IAIProvider ai,
        ITenantContext tenant,
        IDateTimeProvider clock,
        ILogger<RecommendationService> logger)
    {
        _db = db;
        _search = search;
        _ai = ai;
        _tenant = tenant;
        _clock = clock;
        _logger = logger;
    }

    public bool ProviderConfigured => _ai.IsConfigured;

    /// <summary>
    /// Ranks the candidates for one requirement.
    /// </summary>
    /// <param name="requirement">The requirement, already loaded and tenant-checked.</param>
    /// <param name="query">The catalog query derived from it by the caller.</param>
    /// <param name="refresh">True to ignore any stored answer and ask again.</param>
    public async Task<RankingOutcome> RankAsync(
        CustomerRequirement requirement,
        VehicleSearchQuery query,
        bool refresh,
        CancellationToken ct = default)
    {
        var found = await _search
            .SearchAsync(query with { Page = 1, PageSize = MaxCandidates }, ct)
            .ConfigureAwait(false);

        var hits = found.Hits;

        if (hits.Count == 0)
        {
            return new RankingOutcome([], RecommendationSource.Deterministic, null, false);
        }

        var brief = Brief(requirement);
        var candidates = hits.Select(Candidate).ToList();
        var request = new RankingRequest { Requirement = brief, Candidates = candidates };
        var hash = Fingerprint(brief, candidates);

        if (!refresh)
        {
            var stored = await StoredAsync(requirement.Id, hash, hits, ct).ConfigureAwait(false);

            if (stored is not null)
            {
                return stored;
            }
        }

        if (!_ai.IsConfigured)
        {
            return Fallback(hits, "No AI provider is configured, so these are in price order.");
        }

        var audit = new AIRequest
        {
            TenantId = _tenant.TenantId,
            Provider = _ai.Name,
            Model = _ai.Model ?? "unknown",
            Operation = "rank",
            InputHash = hash,

            // Safe to store in full: RequirementBrief has no field for a name, a number or free
            // text, which is the property open item O4 warns this column otherwise violates.
            InputMetadataJson = JsonSerializer.Serialize(brief),
            Status = AIRequestStatus.Pending,
        };

        _db.AIRequests.Add(audit);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        AIRankingResult result;

        try
        {
            result = await _ai.RankAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad. Every provider throws its own family of transport failures,
            // and the answer to all of them is identical - fall back and record why. Letting
            // one escape would turn a slow network into a 500 on a screen that had a perfectly
            // good list to show.
            _logger.LogWarning(ex, "Ranking call to {Provider} threw.", _ai.Name);
            result = AIRankingResult.Failed(ex.Message, _ai.Name, _ai.Model);
        }

        audit.CompletedAtUtc = _clock.UtcNow;
        audit.Cost = result.Usage?.CostUsd;
        audit.TokenUsageJson = result.Usage is null ? null : JsonSerializer.Serialize(result.Usage);

        if (!result.Succeeded)
        {
            audit.Status = AIRequestStatus.Failed;
            audit.FailureReason = Trim(result.Failure);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return Fallback(hits, $"The ranking could not be produced: {result.Failure}");
        }

        var rejection = RankingGuards.Reject(request, result.Ranked!);

        if (rejection is not null)
        {
            // Recorded, not discarded. The call was billed either way, and how often a
            // provider's answers get thrown away is the number that decides whether to keep
            // paying for it.
            audit.Status = AIRequestStatus.Rejected;
            audit.FailureReason = Trim(rejection);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogWarning(
                "Ranking from {Provider} rejected by a guard: {Reason}", _ai.Name, rejection);

            return Fallback(hits, $"The ranking was not used: {rejection}");
        }

        audit.Status = AIRequestStatus.Succeeded;
        audit.OutputMetadataJson = JsonSerializer.Serialize(result.Ranked);

        await PersistAsync(requirement, hits, result.Ranked!, audit, ct).ConfigureAwait(false);

        var byId = hits.ToDictionary(h => h.PublicId);

        var entries = result.Ranked!
            .OrderBy(r => r.Rank)
            .Select(r => new RankedEntry(byId[r.Id], r.Rank, r.Score, [.. r.Reasons]))
            .ToList();

        return new RankingOutcome(entries, RecommendationSource.Ai, null, false);
    }

    /// <summary>The deterministic order, which is what the matches screen shows today.</summary>
    private static RankingOutcome Fallback(IReadOnlyList<VehicleSearchHit> hits, string notice)
        => new(
            [.. hits.Select((h, i) => new RankedEntry(h, i + 1, 0m, []))],
            RecommendationSource.Deterministic,
            notice,
            false);

    /// <summary>A stored answer to this exact question, if there is one.</summary>
    private async Task<RankingOutcome?> StoredAsync(
        long requirementId,
        string hash,
        IReadOnlyList<VehicleSearchHit> hits,
        CancellationToken ct)
    {
        var rows = await _db.VehicleRecommendations
            .AsNoTracking()
            .Where(r => r.CustomerRequirementId == requirementId
                && r.Source == RecommendationSource.Ai
                && r.AIRequest != null
                && r.AIRequest.InputHash == hash
                && r.AIRequest.Status == AIRequestStatus.Succeeded)
            .OrderBy(r => r.Rank)
            .Select(r => new { r.VehicleId, r.Rank, r.Score, r.ReasonsJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return null;
        }

        // The stored ranking is keyed by internal vehicle id; the hits carry public ones. Map
        // through the database rather than trusting the two lists to be in the same order.
        var ids = rows.Select(r => r.VehicleId).ToList();

        var publicIds = await _db.Vehicles
            .AsNoTracking()
            .Where(v => ids.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.PublicId, ct)
            .ConfigureAwait(false);

        var byPublicId = hits.ToDictionary(h => h.PublicId);
        var entries = new List<RankedEntry>(rows.Count);

        foreach (var row in rows)
        {
            if (!publicIds.TryGetValue(row.VehicleId, out var publicId)
                || !byPublicId.TryGetValue(publicId, out var hit))
            {
                // The candidate set has moved since this was stored - a car was merged away or
                // stopped matching. The hash should have caught that; if it did not, the stored
                // answer is stale rather than wrong, and asking again is the honest response.
                return null;
            }

            entries.Add(new RankedEntry(
                hit,
                row.Rank,
                row.Score,
                row.ReasonsJson is null ? [] : JsonSerializer.Deserialize<string[]>(row.ReasonsJson) ?? []));
        }

        return new RankingOutcome(entries, RecommendationSource.Ai, null, true);
    }

    /// <summary>Replaces this requirement's stored ranking with the new one.</summary>
    private async Task PersistAsync(
        CustomerRequirement requirement,
        IReadOnlyList<VehicleSearchHit> hits,
        IReadOnlyList<RankedVehicle> ranked,
        AIRequest audit,
        CancellationToken ct)
    {
        var publicIds = hits.Select(h => h.PublicId).ToList();

        var vehicleIds = await _db.Vehicles
            .Where(v => publicIds.Contains(v.PublicId))
            .ToDictionaryAsync(v => v.PublicId, v => v.Id, ct)
            .ConfigureAwait(false);

        // Replaced rather than appended: two orderings of the same set is not a history, it is
        // an ambiguity about which one the salesperson actually saw.
        var previous = await _db.VehicleRecommendations
            .Where(r => r.CustomerRequirementId == requirement.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _db.VehicleRecommendations.RemoveRange(previous);

        foreach (var entry in ranked)
        {
            if (!vehicleIds.TryGetValue(entry.Id, out var vehicleId))
            {
                continue;
            }

            _db.VehicleRecommendations.Add(new VehicleRecommendation
            {
                TenantId = _tenant.TenantId,
                CustomerRequirementId = requirement.Id,
                VehicleId = vehicleId,
                Rank = entry.Rank,
                Score = entry.Score,
                ReasonsJson = JsonSerializer.Serialize(entry.Reasons),
                Source = RecommendationSource.Ai,
                AIRequest = audit,
            });
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fingerprints the question, so an unchanged one is not paid for twice.
    /// </summary>
    /// <remarks>
    /// Covers the requirement, the candidate ids in order, the model and the prompt version.
    /// The prompt version matters most and is easiest to forget: a reworded prompt is a
    /// different question, and serving the old answer to it would be answering something
    /// nobody asked.
    /// </remarks>
    private string Fingerprint(RequirementBrief brief, IReadOnlyList<CandidateVehicle> candidates)
    {
        var material = new StringBuilder()
            .Append(RankingPrompt.Version).Append('|')
            .Append(_ai.Name).Append('|')
            .Append(_ai.Model).Append('|')
            .Append(JsonSerializer.Serialize(brief)).Append('|')
            .Append(string.Join(',', candidates.Select(c => c.Id)))
            .ToString();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static RequirementBrief Brief(CustomerRequirement r) => new()
    {
        Make = r.Make,
        Model = r.Model,
        Variant = r.Variant,
        BodyType = r.BodyType,
        MinYear = r.MinYear,
        MaxYear = r.MaxYear,
        MinMileage = r.MinMileage,
        MaxMileage = r.MaxMileage,

        // Through SpecWords, as everywhere else prose is composed on this side. An enum name
        // in a prompt is a weaker instruction than the word the trade uses, quite apart from
        // what happens if the model echoes it back into a reason.
        Transmission = r.Transmission is { } t && t != Domain.Enums.Transmission.Unknown
            ? SpecWords.Of(t)
            : null,
        FuelType = r.FuelType is { } f && f != Domain.Enums.FuelType.Unknown
            ? SpecWords.Of(f)
            : null,
        MinPrice = r.MinPrice,
        MaxPrice = r.MaxPrice,
        DestinationCountryCode = r.DestinationCountryCode,

        // RawRequirementText is deliberately not mapped. See RequirementBrief's remarks: it is
        // free text a person typed, which is where a phone number ends up in this trade.
    };

    private static CandidateVehicle Candidate(VehicleSearchHit h) => new()
    {
        Id = h.PublicId,
        Make = h.Make,
        Model = h.Model,
        Variant = h.Variant,
        Year = h.ModelYear,
        Mileage = h.Mileage,
        MileageUnit = h.MileageUnit == MileageUnit.Miles ? "mi" : "km",
        Fuel = h.FuelType == FuelType.Unknown ? null : SpecWords.Of(h.FuelType),
        Transmission = h.Transmission == Domain.Enums.Transmission.Unknown
            ? null
            : SpecWords.Of(h.Transmission),
        Steering = h.SteeringSide == SteeringSide.Unknown ? null : SpecWords.Of(h.SteeringSide),
        SourcePrice = h.PriceBaseCurrency,
        SourceCurrency = h.BaseCurrencyCode,
        RetailPrice = h.TenantPrice,
        RetailCurrency = h.TenantCurrencyCode,
        OfferCount = h.OfferCount,
    };

    private static string? Trim(string? text)
        => text is null ? null : text.Length <= 1000 ? text : text[..1000];
}
