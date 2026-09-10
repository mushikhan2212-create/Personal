using System.Text;
using System.Text.Json;

// Imported rather than qualified at the use site: the `System` constant below shadows the
// namespace of the same name inside this class, so `System.Text.Json...` resolves to the
// string and fails to compile.
using System.Text.Json.Serialization;

namespace CarDealer.Application.AI;

/// <summary>
/// The wording and the output contract, in one place for every provider.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than duplicated per adapter, because the instructions and the JSON shape
/// <b>are</b> the feature - the adapters are transport. Two copies would drift, and the drift
/// would show up as one provider mysteriously scoring worse than another when the real
/// difference was a sentence somebody edited on one side only.
/// </para>
///
/// <para>
/// <see cref="Version"/> is part of the cache key. A reworded prompt is a different question,
/// so a stored answer to the old one must not be served as though it answered the new one.
/// </para>
/// </remarks>
public static class RankingPrompt
{
    /// <summary>Bump on any change to the instructions or the schema below.</summary>
    public const string Version = "rank-v5";

    /// <summary>
    /// The standing instructions. Stable across calls, which is what makes it cacheable.
    /// </summary>
    public const string System = """
        You rank used vehicles for a broker who sells Japanese export stock to overseas buyers.

        You are given a customer's requirement and a list of candidate vehicles. The candidates
        have already passed the broker's own filters. Your job is to put them in the order the
        broker should show them, and to say briefly why.

        Rules, in order of importance:

        1. Rank every candidate you are given, and rank nothing else. Do not add a vehicle, do
           not leave one out, and do not invent an id. Use the ids exactly as supplied.
        2. State only what the data says. Never state a price, year, mileage or specification
           that is not in that vehicle's own fields, and never guess at anything absent - a
           missing colour is missing, not "likely white".
        3. Quote figures, do not calculate them. Write "under the 7,000 budget", not "200 under
           budget" - every number you write must appear verbatim in that vehicle's fields or in
           the requirement. Comparative words are fine without arithmetic: "the lower mileage
           of the two" needs no figure at all.
        4. Never put another vehicle's figures in this vehicle's reasons. Compare in words -
           "the lowest mileage of the group", "the newest here" - and never "newer than the
           2016 one" or "12,000 km less than the other Axio".
        5. Cheapest first, among cars that fit equally well. Price is what the broker would
           quote: the retail price where a car has one, and the source price where it does not.
           Where two cars cost the same, prefer the lower mileage, then the newer.
        6. Passing a limit is not the same as fitting it. A car just under the customer's
           mileage ceiling or at the oldest year they accepted fits worse than one comfortably
           inside, so rank it below - even though it is cheaper and even though it passed. Only
           cars that sit comfortably inside every limit compete on price alone.
        7. A vehicle offered by more than one source is more likely to still be available. That
           is weak evidence about availability only - it says nothing about condition.
        8. Reasons are for the broker, not the customer. Write two or three short phrases, no
           sentences of praise, no sales language. "48,000 km, well under the limit" is useful;
           "a fantastic opportunity" is not. Write plain English and never name a field from
           the data you were given - not minYear, maxPrice, offerCount, fuelType or any other.
           Say what it means: "3 sources list it", not "offerCount 3"; "within the years they
           asked for", not "meets minYear"; "under their budget", not "under maxPrice".

        Score each vehicle from 0 to 1 for how well it fits the requirement. Rank 1 is the best
        fit. Ranks must run 1, 2, 3 with no gaps and no repeats.
        """;

    /// <summary>
    /// The JSON schema the response is constrained to.
    /// </summary>
    /// <remarks>
    /// Returned as a dictionary so each adapter can hand it to whatever its own SDK wants -
    /// Anthropic's <c>output_config.format</c> and an OpenAI-compatible
    /// <c>response_format.json_schema</c> take the same schema in different envelopes.
    /// </remarks>
    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["ranking"],
          "properties": {
            "ranking": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "rank", "score", "reasons"],
                "properties": {
                  "id": { "type": "string" },
                  "rank": { "type": "integer", "minimum": 1 },
                  "score": { "type": "number", "minimum": 0, "maximum": 1 },
                  "reasons": {
                    "type": "array",
                    "items": { "type": "string" },
                    "minItems": 1,
                    "maxItems": 3
                  }
                }
              }
            }
          }
        }
        """);

    /// <summary>
    /// <see cref="Schema"/> as a property bag.
    /// </summary>
    /// <remarks>
    /// The Anthropic SDK takes the schema as a dictionary of its top-level members rather than
    /// as one JSON object, so this is the same schema wearing the shape that SDK wants. Kept
    /// beside the schema rather than in the adapter so there is still only one schema.
    /// </remarks>
    public static Dictionary<string, JsonElement> SchemaMembers()
        => Schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

    /// <summary>
    /// The per-call half: the requirement and the candidates, as compact JSON.
    /// </summary>
    /// <remarks>
    /// JSON rather than prose because the candidate list is a table and prose would only make
    /// it longer. Written after any cached prefix, since it changes on every call.
    /// </remarks>
    public static string User(RankingRequest request)
    {
        var payload = new
        {
            requirement = request.Requirement,
            candidates = request.Candidates,
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        return new StringBuilder()
            .AppendLine("Requirement and candidates:")
            .AppendLine()
            .AppendLine(json)
            .AppendLine()
            .Append("Return the ranking for all ")
            .Append(request.Candidates.Count)
            .Append(" candidates.")
            .ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Nulls dropped: a catalogue this sparse would otherwise spend a third of the payload
        // saying "colour: null", and an absent field reads as absent more clearly than a null.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
