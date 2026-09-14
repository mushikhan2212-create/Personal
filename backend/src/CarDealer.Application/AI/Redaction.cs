using System.Text.RegularExpressions;

namespace CarDealer.Application.AI;

/// <summary>What was taken out of a message, and what was left.</summary>
/// <param name="Text">The message with identifiers replaced by placeholders.</param>
/// <param name="Removed">How many identifiers were replaced, for telling the operator.</param>
public sealed record RedactedText(string Text, int Removed);

/// <summary>
/// Strips the person out of a customer's own words, before those words leave the building.
/// </summary>
/// <remarks>
/// <para>
/// The enforcement half of open item O4. The ranking feature never needed this because
/// <see cref="RequirementBrief"/> has nowhere to put a name; extraction cannot dodge it the same
/// way, because the customer's message <b>is</b> the input. So the message is cleaned first, and
/// the raw text never goes to a provider and is never stored in the audit row.
/// </para>
///
/// <para>
/// <b>This reduces exposure. It does not eliminate it, and it is not a compliance control.</b>
/// A pattern catches a phone number written as digits and cannot catch one written as words, or
/// "my brother Asif's shop on Mall Road". The product owner accepted that when choosing this
/// over sending raw text; the limitation is recorded here rather than in a commit message
/// because whoever changes this file needs to know what it was ever claimed to do.
/// </para>
///
/// <para>
/// Where a pattern is ambiguous it errs towards removing too much. Over-redaction loses a field
/// the operator then types in themselves; under-redaction is the failure this exists to prevent.
/// </para>
/// </remarks>
public static partial class Redaction
{
    // Placeholders rather than deletion: a gap reads as a joined-up sentence and invites the
    // model to infer across it, where a marker says plainly that something was here and was not
    // its business. They are also what the guards look for leaking back into a field.
    private const string Email = "[email]";
    private const string Phone = "[phone]";
    private const string Id = "[id]";
    private const string Name = "[name]";

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    /// <summary>Chat links, which carry a phone number in the path.</summary>
    [GeneratedRegex(@"(?:https?://)?(?:wa\.me|t\.me|m\.me)/\S+", RegexOptions.IgnoreCase)]
    private static partial Regex ChatLinkPattern();

    /// <summary>A Pakistani CNIC, which has a shape nothing else here shares.</summary>
    [GeneratedRegex(@"\b\d{5}-\d{7}-\d\b")]
    private static partial Regex NationalIdPattern();

    /// <summary>
    /// A number with an international dialling code.
    /// </summary>
    /// <remarks>
    /// Each group is bounded on purpose. An open-ended run of digits and spaces would swallow
    /// whatever followed the number - "+92 300 1234567 aur 2017 model" would lose the year, and
    /// a redactor that eats the answer is worse than no feature.
    /// </remarks>
    [GeneratedRegex(@"\+\d{1,3}[\s-]?\(?\d{2,4}\)?[\s-]?\d{3,4}[\s-]?\d{3,4}")]
    private static partial Regex InternationalPhonePattern();

    /// <summary>A local number. Prices and mileages do not begin with a zero.</summary>
    [GeneratedRegex(@"\b0\d{2,4}[\s-]?\d{6,8}\b")]
    private static partial Regex LocalPhonePattern();

    /// <summary>Thirteen bare digits: a CNIC written without its separators.</summary>
    [GeneratedRegex(@"\b\d{13}\b")]
    private static partial Regex BareNationalIdPattern();

    /// <summary>
    /// Ten to twelve bare digits.
    /// </summary>
    /// <remarks>
    /// Above anything this trade writes down. A dear car is eight digits of rupees and the
    /// highest mileage in the catalogue is six, so a run this long is an identifier.
    /// </remarks>
    [GeneratedRegex(@"\b\d{10,12}\b")]
    private static partial Regex BarePhonePattern();

    /// <summary>
    /// Cleans a message for sending.
    /// </summary>
    /// <param name="text">What the customer wrote.</param>
    /// <param name="names">
    /// Names to remove as well - the customer's own, from their record. A name cannot be found
    /// by pattern, but it can be looked up: the operator opened this customer to paste the
    /// message, so the one name almost certain to appear is already known.
    /// </param>
    public static RedactedText Apply(string? text, IEnumerable<string>? names = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new RedactedText(string.Empty, 0);
        }

        var removed = 0;
        var working = text;

        // Ordered longest-shape-first so a broader pattern cannot claim part of a narrower one.
        // The chat link goes before the bare-digit rules or it would keep its "wa.me/" prefix
        // while losing the digits, which leaks the fact of the channel for no benefit.
        working = Replace(working, EmailPattern(), Email, ref removed);
        working = Replace(working, ChatLinkPattern(), Phone, ref removed);
        working = Replace(working, NationalIdPattern(), Id, ref removed);
        working = Replace(working, InternationalPhonePattern(), Phone, ref removed);
        working = Replace(working, LocalPhonePattern(), Phone, ref removed);
        working = Replace(working, BareNationalIdPattern(), Id, ref removed);
        working = Replace(working, BarePhonePattern(), Phone, ref removed);

        foreach (var token in NameTokens(names))
        {
            working = Replace(
                working,
                new Regex($@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase),
                Name,
                ref removed);
        }

        return new RedactedText(working, removed);
    }

    /// <summary>True when a value handed back still carries a placeholder.</summary>
    /// <remarks>
    /// A model that puts "[phone]" in a field is not leaking anything, but it is reporting a
    /// redaction as though it were the customer's answer. The guards reject it.
    /// </remarks>
    public static bool ContainsPlaceholder(string? value)
        => value is not null
            && (value.Contains(Email, StringComparison.OrdinalIgnoreCase)
                || value.Contains(Phone, StringComparison.OrdinalIgnoreCase)
                || value.Contains(Id, StringComparison.OrdinalIgnoreCase)
                || value.Contains(Name, StringComparison.OrdinalIgnoreCase));

    private static string Replace(string text, Regex pattern, string with, ref int removed)
    {
        removed += pattern.Matches(text).Count;

        return pattern.Replace(text, with);
    }

    /// <summary>
    /// The parts of a name worth removing.
    /// </summary>
    /// <remarks>
    /// Two characters and under are dropped: initials and particles collide with ordinary words
    /// and would shred the message. A known collision is left standing - a customer named Mehran
    /// asking after a Mehran loses both to <c>[name]</c>, and the operator retypes the make.
    /// That is the over-redaction side of the trade, which is the side to be on.
    /// </remarks>
    private static IEnumerable<string> NameTokens(IEnumerable<string>? names)
        => (names ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .SelectMany(n => n.Split([' ', '\t', '.', ','], StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Trim())
            .Where(t => t.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
