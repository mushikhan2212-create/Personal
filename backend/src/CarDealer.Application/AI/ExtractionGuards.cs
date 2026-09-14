using System.Globalization;
using System.Text.RegularExpressions;

namespace CarDealer.Application.AI;

/// <summary>
/// The checks an extracted requirement has to pass before an operator is shown it.
/// </summary>
/// <remarks>
/// <para>
/// The same bargain as <see cref="RankingGuards"/>, against a worse failure. A ranking that goes
/// wrong reorders cars the filters already admitted; an extraction that goes wrong writes a
/// constraint into a customer's record that the customer never stated - and then every search,
/// every alert and every shortlist from that day on quietly answers the wrong question. Nobody
/// re-reads the message to check.
/// </para>
///
/// <para>
/// <see cref="ExtractedField.Evidence"/> is what makes that checkable at all. A figure alone
/// cannot be told apart from an invented one; a figure with the words it came from can be, by
/// looking for those words in the message. That check is the centre of this file and the reason
/// the response schema asks for evidence at all.
/// </para>
/// </remarks>
public static partial class ExtractionGuards
{
    /// <summary>Years outside this are a typo or a hallucination, never a car.</summary>
    private const int OldestYear = 1950;
    private const int NewestYear = 2100;

    /// <summary>Past this, the odometer is broken or the figure is not a mileage.</summary>
    private const int HighestMileage = 2_000_000;

    /// <summary>A price above this is a misplaced decimal in any currency this trade uses.</summary>
    private const decimal HighestPrice = 1_000_000_000m;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Checks an extraction against the message it claims to have read.
    /// </summary>
    /// <returns>Null when it is sound, otherwise why it was rejected.</returns>
    public static string? Reject(ExtractionRequest request, IReadOnlyList<ExtractedField> fields)
    {
        var haystack = Normalise(request.Text);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            // 1. A field the requirement has no room for. The schema constrains this, and a
            //    provider that ignores response_format ignores that too.
            if (!ExtractionFields.All.Contains(field.Field, StringComparer.Ordinal))
            {
                return $"The extraction returned \"{field.Field}\", which is not a field of a "
                    + "requirement.";
            }

            // 2. The same field twice. Two answers to one question is not an answer, and
            //    silently taking the first would pick by array order.
            if (!seen.Add(field.Field))
            {
                return $"The extraction gave two values for \"{field.Field}\".";
            }

            if (string.IsNullOrWhiteSpace(field.Value))
            {
                return $"The extraction returned \"{field.Field}\" with no value.";
            }

            // 3. A redaction placeholder reported as the customer's answer. Nothing leaks, but
            //    "[phone]" is not a make and the operator should never be offered it.
            if (Redaction.ContainsPlaceholder(field.Value)
                || Redaction.ContainsPlaceholder(field.Evidence))
            {
                return $"The extraction put a removed detail in \"{field.Field}\".";
            }

            // 4. The evidence is in the message. This is the one that catches invention: a
            //    budget the customer never named has no words behind it.
            if (string.IsNullOrWhiteSpace(field.Evidence))
            {
                return $"The extraction gave no evidence for \"{field.Field}\".";
            }

            if (!haystack.Contains(Normalise(field.Evidence), StringComparison.OrdinalIgnoreCase))
            {
                return $"The evidence for \"{field.Field}\" - \"{Trim(field.Evidence)}\" - is not "
                    + "in the message.";
            }

            // 5. The value is the kind of thing the field holds, and within reach of reality.
            if (Unusable(field) is { } bad)
            {
                return bad;
            }
        }

        return null;
    }

    /// <summary>Why a value cannot be stored in its field, if it cannot.</summary>
    private static string? Unusable(ExtractedField field)
    {
        if (ExtractionFields.Whole.Contains(field.Field, StringComparer.Ordinal))
        {
            if (!int.TryParse(
                    field.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var whole))
            {
                return $"\"{field.Field}\" came back as \"{Trim(field.Value)}\", which is not a "
                    + "whole number.";
            }

            var year = field.Field is ExtractionFields.MinYear or ExtractionFields.MaxYear;

            return year switch
            {
                true when whole < OldestYear || whole > NewestYear =>
                    $"\"{field.Field}\" came back as {whole}, which is not a model year.",
                false when whole < 0 || whole > HighestMileage =>
                    $"\"{field.Field}\" came back as {whole}, which is not a mileage.",
                _ => null,
            };
        }

        if (ExtractionFields.Money.Contains(field.Field, StringComparer.Ordinal))
        {
            if (!decimal.TryParse(
                    field.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var money))
            {
                return $"\"{field.Field}\" came back as \"{Trim(field.Value)}\", which is not an "
                    + "amount.";
            }

            if (money <= 0m || money > HighestPrice)
            {
                return $"\"{field.Field}\" came back as {money}, which is not a price.";
            }
        }

        // Currency is checked for shape rather than against a list. A three-letter code that is
        // not a real one is the operator's to notice; a sentence in this field is a mistake the
        // screen should never have to render.
        if (field.Field == ExtractionFields.PriceCurrency && field.Value.Trim().Length != 3)
        {
            return $"The currency came back as \"{Trim(field.Value)}\", which is not a code.";
        }

        if (field.Field == ExtractionFields.DestinationCountryCode
            && field.Value.Trim().Length != 2)
        {
            return $"The destination came back as \"{Trim(field.Value)}\", which is not a "
                + "two-letter country code.";
        }

        return null;
    }

    /// <summary>
    /// Whitespace flattened so a reflowed quotation still matches.
    /// </summary>
    /// <remarks>
    /// Models rewrap text. Requiring the line breaks to survive would reject honest evidence for
    /// a reason that has nothing to do with whether the customer said it.
    /// </remarks>
    private static string Normalise(string text)
        => Whitespace().Replace(text, " ").Trim();

    private static string Trim(string text)
        => text.Length <= 60 ? text : text[..60] + "...";
}
