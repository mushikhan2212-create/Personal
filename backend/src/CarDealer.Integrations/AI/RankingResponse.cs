using System.Text.Json;
using System.Text.Json.Serialization;
using CarDealer.Application.AI;

namespace CarDealer.Integrations.AI;

/// <summary>
/// Turns whatever JSON a provider returned into a ranking, or says why it could not.
/// </summary>
/// <remarks>
/// Shared by both adapters, because the wire format differs but the payload does not - the
/// schema in <see cref="RankingPrompt"/> is the same on either side, so parsing it twice would
/// only create two ways to be wrong about the same thing.
/// </remarks>
internal static class RankingResponse
{
    private sealed record Envelope
    {
        [JsonPropertyName("ranking")]
        public List<Entry>? Ranking { get; init; }
    }

    private sealed record Entry
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("rank")]
        public int Rank { get; init; }

        [JsonPropertyName("score")]
        public decimal Score { get; init; }

        [JsonPropertyName("reasons")]
        public List<string>? Reasons { get; init; }
    }

    /// <summary>
    /// Parses a response body.
    /// </summary>
    /// <returns>
    /// The ranking, or null with <paramref name="failure"/> set. A parse failure is an ordinary
    /// outcome here rather than an exception: the caller's answer to it is the same as its
    /// answer to a timeout, and a structured output that is not structured is exactly the kind
    /// of thing a weaker model does.
    /// </returns>
    public static IReadOnlyList<RankedVehicle>? Parse(string? json, out string? failure)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            failure = "The provider returned an empty response.";
            return null;
        }

        Envelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json);
        }
        catch (JsonException ex)
        {
            failure = $"The response was not valid JSON: {ex.Message}";
            return null;
        }

        if (envelope?.Ranking is not { Count: > 0 })
        {
            failure = "The response carried no ranking.";
            return null;
        }

        var ranked = new List<RankedVehicle>(envelope.Ranking.Count);

        foreach (var entry in envelope.Ranking)
        {
            // Parsed rather than trusted. An id that is not a GUID cannot match a candidate, so
            // catching it here gives a clearer failure than letting the guard report it as an
            // invented vehicle - which would be true but less useful.
            if (!Guid.TryParse(entry.Id, out var id))
            {
                failure = $"The response carried an id that is not a vehicle id: '{entry.Id}'.";
                return null;
            }

            ranked.Add(new RankedVehicle
            {
                Id = id,
                Rank = entry.Rank,
                Score = entry.Score,
                Reasons = entry.Reasons ?? [],
            });
        }

        failure = null;
        return ranked;
    }
}
