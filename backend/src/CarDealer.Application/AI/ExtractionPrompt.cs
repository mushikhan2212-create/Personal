using System.Text.Json;

namespace CarDealer.Application.AI;

/// <summary>
/// What the model is asked when it reads a customer's message.
/// </summary>
/// <remarks>
/// Written after the lesson of D19: a prompt asks, it does not enforce. Everything here that
/// matters is checked afterwards by <see cref="ExtractionGuards"/>, and the rules below exist to
/// make a good answer likely rather than to make a bad one impossible.
/// </remarks>
public static class ExtractionPrompt
{
    /// <summary>Bump on any change to the instructions or the schema below.</summary>
    public const string Version = "extract-v1";

    public const string System = """
        You read one message from a customer of a used-car broker and pull out what they are
        asking for. The broker sells Japanese export stock to overseas buyers, and the message
        may be in English, Urdu written in Latin letters, or a mixture.

        Return only fields the message actually states, with the words you read each one from.

        Rules, in order of importance:

        1. If the message does not say it, do not return it. A customer who named no budget has
           no budget. Returning a field they did not state is the worst thing you can do here,
           because it looks exactly like one they did.
        2. Evidence must be copied from the message, character for character. It is how the
           broker checks you, so a paraphrase is worse than useless. Keep it short - the few
           words the field came from, not the whole sentence.
        3. Do not interpret vague wishes as figures. "A newer model", "low mileage", "cheap",
           "good condition" state no number. Leave them out.
        4. Convert to the unit the field is stored in, and leave the evidence as written. "35
           lakh" is 3500000, evidence "35 lakh". "1.2 million" is 1200000. "80k km" is 80000.
           Miles are not kilometres: if the customer says miles, say so in the evidence and give
           the figure they said.
        5. Square brackets mark details that were removed before you saw the message - [name],
           [phone], [email], [id]. They are not information. Never put one in a value, and never
           guess what was behind one.
        6. A year is a year and a price is a price. "2017 ya newer" is minYear 2017. "under 35
           lakh" is maxPrice. "2017 model, 35 lakh tak" states both; do not let one become the
           other.
        7. Give the currency separately when the customer names or implies one - lakh, crore,
           rupees and PKR all mean PKR; yen or JPY mean JPY. If no currency is discernible,
           leave priceCurrency out rather than assuming.
        8. Make and model are the manufacturer and the car. "Corolla Axio" is make Toyota, model
           Corolla Axio - the make may be implied by a model only that trade knows, and that is
           the one inference you may make.
        """;

    /// <summary>The JSON schema the response is constrained to.</summary>
    /// <remarks>
    /// A flat list rather than an object of nullable fields, so an absent field is absent rather
    /// than present and null. It also keeps the schema short, which matters on an account whose
    /// whole minute's allowance is a thousand tokens.
    /// </remarks>
    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["fields"],
          "properties": {
            "fields": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["field", "value", "evidence"],
                "properties": {
                  "field": {
                    "type": "string",
                    "enum": [{{Enumerated}}]
                  },
                  "value": { "type": "string" },
                  "evidence": { "type": "string" }
                }
              }
            }
          }
        }
        """);

    /// <summary><see cref="Schema"/> as a property bag, for the Anthropic SDK's shape.</summary>
    public static Dictionary<string, JsonElement> SchemaMembers()
        => Schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

    /// <summary>The per-call half: the message, already redacted.</summary>
    public static string User(ExtractionRequest request)
        => $"""
            The customer's message:

            {request.Text}

            Return the fields this message states.
            """;

    /// <summary>
    /// The field names, as the schema's enum.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="ExtractionFields.All"/> rather than written out again, so a field
    /// added there cannot be one the schema silently refuses.
    /// </remarks>
    private static string Enumerated
        => string.Join(", ", ExtractionFields.All.Select(f => $"\"{f}\""));
}
