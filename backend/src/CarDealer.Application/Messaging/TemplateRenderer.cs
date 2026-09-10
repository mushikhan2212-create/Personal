using System.Text;
using System.Text.RegularExpressions;

namespace CarDealer.Application.Messaging;

/// <summary>One placeholder a template may carry, as offered to whoever is editing one.</summary>
/// <param name="Name">The canonical spelling, without braces.</param>
/// <param name="Description">What it fills in, in the words the editor screen shows.</param>
/// <param name="NeedsVehicle">True if it cannot resolve without a car in hand.</param>
public sealed record TemplatePlaceholder(string Name, string Description, bool NeedsVehicle);

/// <summary>
/// Turns a stored template into the text that appears in the compose box.
/// </summary>
/// <remarks>
/// <para>
/// Two rules do the real work, and both exist because the alternative reaches a customer.
/// </para>
///
/// <para>
/// <b>An empty placeholder takes its line with it.</b> Half this catalogue is missing a field:
/// the POC measured variant absent on 42% of listings and colour and engine size absent on
/// whole sources. A template line reading <c>Year: {Year}</c> against a car with no year would
/// otherwise send <c>Year:</c> - which looks like the dealer forgot rather than like the data
/// is thin. Dropping the line is the honest rendering, and it is what the hand-written composer
/// this replaces already did per-field.
/// </para>
///
/// <para>
/// <b>A placeholder may carry a fallback</b>, written <c>{FirstName|there}</c>. Without it the
/// line-drop rule is too blunt for the one line that must always survive: a greeting is not
/// optional, and a message that opens mid-sentence because a customer has no first name
/// recorded is worse than one that opens "Hi there". The fallback makes the author's intent
/// explicit rather than having the renderer guess which lines are structural.
/// </para>
///
/// <para>
/// The placeholder set is closed and <see cref="UnknownPlaceholders"/> is checked before a
/// template is stored. An unrecognised name is a typo the author cannot see - it renders
/// literally, so <c>{Modle}</c> would go out in a message with the braces still on it.
/// </para>
/// </remarks>
public static class TemplateRenderer
{
    /// <summary>
    /// <c>{Name}</c> or <c>{Name|fallback}</c>. The fallback runs to the closing brace, so it
    /// may contain spaces and punctuation but not a nested placeholder.
    /// </summary>
    private static readonly Regex Token = new(
        @"\{(?<name>[A-Za-z][A-Za-z0-9]*)(?:\|(?<fallback>[^}]*))?\}",
        RegexOptions.Compiled);

    /// <summary>Everything a template may reference, in the order the editor lists them.</summary>
    public static readonly IReadOnlyList<TemplatePlaceholder> Placeholders =
    [
        new("FirstName", "The customer's first name", false),
        new("LastName", "The customer's last name", false),
        new("City", "The customer's city", false),
        new("Country", "The customer's country", false),
        new("DealerName", "Your business name", false),

        new("Vehicle", "Make, model and variant together, e.g. Toyota Corolla Altis", true),
        new("Make", "e.g. Toyota", true),
        new("Model", "e.g. Corolla", true),
        new("Variant", "e.g. Altis", true),
        new("Year", "Model year", true),
        new("Mileage", "Odometer, with its unit", true),
        new("Colour", "Exterior colour", true),
        new("Fuel", "Petrol, diesel, hybrid…", true),
        new("Transmission", "Automatic, manual, CVT…", true),
        new("Steering", "Right-hand or left-hand drive", true),
        new("Drivetrain", "FWD, RWD, AWD, 4WD", true),
        new("BodyType", "Sedan, hatchback, van…", true),
        new("Engine", "Engine size in cc", true),

        new("Price", "Your retail price for this car, if you have set one", true),
        new("ListingUrl", "The source listing's web address - see the warning on this one", true),
    ];

    private static readonly HashSet<string> Known =
        new(Placeholders.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The placeholder names in <paramref name="body"/> that nothing can fill in.
    /// </summary>
    /// <remarks>
    /// Returned rather than thrown so the caller can name every one of them at once. Somebody
    /// fixing a template wants the whole list, not the first mistake and another round trip.
    /// </remarks>
    public static IReadOnlyList<string> UnknownPlaceholders(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        return [.. Token.Matches(body)
            .Select(m => m.Groups["name"].Value)
            .Where(name => !Known.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>True if the template cannot be rendered without a car.</summary>
    public static bool NeedsVehicle(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        var vehicleFields = Placeholders
            .Where(p => p.NeedsVehicle)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Token.Matches(body)
            .Any(m => vehicleFields.Contains(m.Groups["name"].Value));
    }

    /// <summary>
    /// Fills <paramref name="body"/> in from <paramref name="values"/>.
    /// </summary>
    /// <remarks>
    /// A value that is absent or blank counts as empty, and an empty placeholder with no
    /// fallback removes the line it appears on. A line with no placeholders is always kept,
    /// including the blank lines that make the message readable.
    /// </remarks>
    public static string Render(string? body, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        // Split on \n after normalising, so a template stored with Windows line endings does
        // not leave a stray \r at the end of every line of a WhatsApp message.
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var dropped = false;

            var rendered = Token.Replace(line, match =>
            {
                var name = match.Groups["name"].Value;

                values.TryGetValue(name, out var value);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value!.Trim();
                }

                // The fallback group is absent when no pipe was written, which is different
                // from a pipe with nothing after it: "{Variant|}" is an author saying "leave
                // it blank and keep the line", and is honoured as such.
                if (match.Groups["fallback"].Success)
                {
                    return match.Groups["fallback"].Value;
                }

                dropped = true;
                return string.Empty;
            });

            if (!dropped)
            {
                kept.Add(rendered);
            }
        }

        return Tidy(kept);
    }

    /// <summary>
    /// Joins the surviving lines without leaving the holes the dropped ones would.
    /// </summary>
    /// <remarks>
    /// Removing a bullet from the middle of a list is invisible; removing the only bullet
    /// between two blank lines leaves a double gap that reads as an unfinished message. So runs
    /// of blank lines collapse to one, and the message is trimmed at both ends.
    /// </remarks>
    private static string Tidy(List<string> lines)
    {
        var text = new StringBuilder();
        var blankRun = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blankRun++;
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(blankRun > 0 ? "\n\n" : "\n");
            }

            text.Append(line.TrimEnd());
            blankRun = 0;
        }

        return text.ToString();
    }
}
