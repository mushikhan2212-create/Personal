using System.Text.Json;
using System.Text.Json.Serialization;
using CarDealer.Application.AI;

namespace CarDealer.Integrations.AI;

/// <summary>
/// Turns whatever JSON a provider returned into extracted fields, or says why it could not.
/// </summary>
/// <remarks>
/// Shared by both adapters for the same reason <see cref="RankingResponse"/> is: the wire
/// envelope differs between them, the payload does not, and two parsers would only be two ways
/// to be wrong about one schema.
/// </remarks>
internal static class ExtractionResponse
{
    private sealed record Envelope
    {
        [JsonPropertyName("fields")]
        public List<Entry>? Fields { get; init; }
    }

    private sealed record Entry
    {
        [JsonPropertyName("field")]
        public string? Field { get; init; }

        [JsonPropertyName("value")]
        public string? Value { get; init; }

        [JsonPropertyName("evidence")]
        public string? Evidence { get; init; }
    }

    /// <summary>
    /// Parses a response body.
    /// </summary>
    /// <returns>
    /// The fields, or null with <paramref name="failure"/> set. An empty list is a success and
    /// not a failure: a message that states no requirement is a real answer, and the prompt asks
    /// for exactly that rather than for something invented to fill the space.
    /// </returns>
    public static IReadOnlyList<ExtractedField>? Parse(string? json, out string? failure)
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

        if (envelope?.Fields is null)
        {
            failure = "The response had no \"fields\" array.";
            return null;
        }

        var parsed = new List<ExtractedField>(envelope.Fields.Count);

        foreach (var entry in envelope.Fields)
        {
            // A null member is a malformed answer rather than an absent field: the schema marks
            // all three required, so reaching here means the provider ignored it. Rejecting is
            // honest - substituting an empty string would hand the guards something to pass.
            if (entry.Field is null || entry.Value is null || entry.Evidence is null)
            {
                failure = "An extracted field was missing its name, value or evidence.";
                return null;
            }

            parsed.Add(new ExtractedField
            {
                Field = entry.Field,
                Value = entry.Value,
                Evidence = entry.Evidence,
            });
        }

        failure = null;
        return parsed;
    }
}
