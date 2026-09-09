using System.Text;

namespace CarDealer.Application.Messaging;

/// <summary>
/// Turns a phone number as somebody typed it into the digits-only international form a
/// messaging link needs.
/// </summary>
/// <remarks>
/// <b>The rule this file exists to enforce: never guess a country.</b> A wa.me link built from
/// the wrong country code does not fail - it opens a chat with a real person somewhere else,
/// who is not the customer. Messaging a stranger is worse than showing a salesperson "we cannot
/// build a link for this number, add the country code", so every ambiguous case refuses.
///
/// <para>
/// A number that already carries its country - <c>+92…</c> or <c>0092…</c> - needs nothing from
/// us and is used as written. A national number such as <c>0300 1234567</c> is resolved only
/// when the customer record says which country they are in, and only for a country in the table
/// below. Anything else is a refusal with a reason.
/// </para>
///
/// <para>
/// Deliberately not libphonenumber. That library is the right answer for validating numbers
/// against per-country numbering plans, and this does not do that: it decides whether a number
/// is unambiguously international, and converts it when it can be. Adding a megabyte of
/// metadata to answer a question this narrow is not a trade worth making, and the refusal path
/// means the cost of being conservative is a message a person retypes rather than one sent to
/// the wrong number.
/// </para>
/// </remarks>
public static class PhoneNumber
{
    /// <summary>E.164 allows at most 15 digits; below about 8 nothing is a real mobile.</summary>
    private const int MinDigits = 8;
    private const int MaxDigits = 15;

    /// <summary>
    /// Country calling codes, for resolving a national number.
    /// </summary>
    /// <remarks>
    /// Not a complete list, and not meant to be. It covers the right-hand-drive import markets
    /// this trade actually serves plus the exporting countries. A country that is absent is a
    /// refusal, never a guess - so extending this table is safe, and forgetting to extend it
    /// costs a clear error message rather than a misdirected message.
    /// </remarks>
    private static readonly Dictionary<string, string> CallingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PK"] = "92",   // Pakistan
        ["JP"] = "81",   // Japan
        ["KE"] = "254",  // Kenya
        ["TZ"] = "255",  // Tanzania
        ["UG"] = "256",  // Uganda
        ["ZM"] = "260",  // Zambia
        ["ZW"] = "263",  // Zimbabwe
        ["MW"] = "265",  // Malawi
        ["MZ"] = "258",  // Mozambique
        ["RW"] = "250",  // Rwanda
        ["BW"] = "267",  // Botswana
        ["NA"] = "264",  // Namibia
        ["ZA"] = "27",   // South Africa
        ["NG"] = "234",  // Nigeria
        ["GH"] = "233",  // Ghana
        ["MU"] = "230",  // Mauritius
        ["LK"] = "94",   // Sri Lanka
        ["BD"] = "880",  // Bangladesh
        ["MM"] = "95",   // Myanmar
        ["PH"] = "63",   // Philippines
        ["IN"] = "91",   // India
        ["AE"] = "971",  // United Arab Emirates
        ["GB"] = "44",   // United Kingdom
        ["IE"] = "353",  // Ireland
        ["CY"] = "357",  // Cyprus
        ["MT"] = "356",  // Malta
        ["AU"] = "61",   // Australia
        ["NZ"] = "64",   // New Zealand
        ["GY"] = "592",  // Guyana
        ["TT"] = "1",    // Trinidad and Tobago
        ["JM"] = "1",    // Jamaica
        ["US"] = "1",
        ["CA"] = "1",
    };

    /// <summary>
    /// The number in digits-only international form, or a reason it cannot be determined.
    /// </summary>
    public static PhoneResolution Resolve(string? phone, string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return PhoneResolution.Refused("This customer has no phone number on file.");
        }

        var raw = phone.Trim();

        // An extension makes the number undialable as written and there is no sane way to send
        // one over WhatsApp, so it is worth saying rather than silently truncating.
        if (raw.Contains("ext", StringComparison.OrdinalIgnoreCase) || raw.Contains('x'))
        {
            return PhoneResolution.Refused(
                $"'{phone}' looks like it has an extension. WhatsApp needs a direct mobile "
                + "number.");
        }

        // The plus is looked for before the first digit rather than at position zero, because
        // "(+92) 300 1234567" is how plenty of contact lists write it - and treating that as a
        // national number would send it down the guess-the-country path for no reason.
        var lead = raw.AsSpan().TrimStart(" \t([".ToCharArray());
        var international = lead.StartsWith("+") || lead.StartsWith("00");
        var digits = Digits(raw);

        if (digits.Length == 0)
        {
            return PhoneResolution.Refused($"'{phone}' has no digits in it.");
        }

        if (international)
        {
            // "0092…" is the same as "+92…"; the leading zeros are an exit prefix, not part of
            // the number.
            var trimmed = lead.StartsWith("+") ? digits : digits[2..];

            return Validate(trimmed, phone);
        }

        if (string.IsNullOrWhiteSpace(countryCode)
            || !CallingCodes.TryGetValue(countryCode.Trim(), out var calling))
        {
            // The refusal that matters. Guessing here is how a message reaches a stranger.
            return PhoneResolution.Refused(
                $"'{phone}' has no country code, and this customer's country "
                + (string.IsNullOrWhiteSpace(countryCode)
                    ? "is not set"
                    : $"('{countryCode}') is not one we know the dialling code for")
                + ". Store the number in international form, like +92 300 1234567.");
        }

        // A single leading zero is the national trunk prefix and is dropped when the country
        // code goes on. "0300 1234567" in Pakistan is "+92 300 1234567".
        var national = digits.TrimStart('0');

        if (national.Length == 0)
        {
            return PhoneResolution.Refused($"'{phone}' is not a usable number.");
        }

        // Already carries its own country code - somebody typed "923001234567" without the
        // plus. Prefixing again would produce "9292…", a number in nobody's country.
        if (digits.StartsWith(calling, StringComparison.Ordinal)
            && digits.Length >= calling.Length + MinDigits - 1)
        {
            return Validate(digits, phone);
        }

        return Validate(calling + national, phone);
    }

    private static PhoneResolution Validate(string digits, string original)
    {
        if (digits.Length is < MinDigits or > MaxDigits)
        {
            return PhoneResolution.Refused(
                $"'{original}' does not look like a full international number "
                + $"({digits.Length} digits).");
        }

        return PhoneResolution.Resolved(digits);
    }

    private static string Digits(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (char.IsDigit(c)) builder.Append(c);
        }

        return builder.ToString();
    }
}

/// <param name="Digits">Digits-only international form, no plus. Null when refused.</param>
/// <param name="Reason">Why no number could be determined. Null when resolved.</param>
public sealed record PhoneResolution(string? Digits, string? Reason)
{
    public bool IsResolved => Digits is not null;

    public static PhoneResolution Resolved(string digits) => new(digits, null);

    public static PhoneResolution Refused(string reason) => new(null, reason);
}
