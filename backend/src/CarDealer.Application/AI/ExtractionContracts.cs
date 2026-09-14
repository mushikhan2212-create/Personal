namespace CarDealer.Application.AI;

/// <summary>
/// A customer's message, on its way to a provider, with the customer taken out of it.
/// </summary>
/// <remarks>
/// <para>
/// The constructor is private and the only way in is <see cref="From"/>, which takes a
/// <see cref="RedactedText"/>. So a caller cannot hand raw text to a provider by forgetting a
/// step: there is no overload that accepts a string, and the type that carries redacted text is
/// the only thing that opens the door.
/// </para>
///
/// <para>
/// The same argument as <see cref="RequirementBrief"/>, made one level further down. That type
/// is safe because it has nowhere to put a name; this one is safe because it has no way in that
/// skips the cleaning. Both make the rule a property of the code rather than a habit.
/// </para>
/// </remarks>
public sealed record ExtractionRequest
{
    private ExtractionRequest(string text) => Text = text;

    /// <summary>The message, redacted. Never the original.</summary>
    public string Text { get; }

    public static ExtractionRequest From(RedactedText redacted) => new(redacted.Text);
}

/// <summary>
/// One thing the model says the customer asked for, and the words it read it in.
/// </summary>
/// <remarks>
/// <see cref="Evidence"/> is the whole design. Asking for a figure alone makes invention
/// invisible - a budget nobody stated looks exactly like one they did. Asking for the words it
/// came from makes invention <b>checkable</b>: the guard looks for those words in the message and
/// throws the answer away when they are not there. It also gives the operator something to read
/// beside each field instead of a number to take on trust.
/// </remarks>
public sealed record ExtractedField
{
    /// <summary>One of <see cref="ExtractionFields.All"/>.</summary>
    public required string Field { get; init; }

    /// <summary>The value, already converted to the unit the field is stored in.</summary>
    public required string Value { get; init; }

    /// <summary>The words from the message this was read from, copied exactly.</summary>
    public required string Evidence { get; init; }
}

/// <summary>The fields a message may fill in, and what each one accepts.</summary>
/// <remarks>
/// Deliberately the columns of <c>CustomerRequirement</c> and nothing else. A model cannot
/// volunteer a field the requirement has no room for, which is the same containment the ranking
/// gets from ranking a set it cannot change.
/// </remarks>
public static class ExtractionFields
{
    public const string Make = "make";
    public const string Model = "model";
    public const string Variant = "variant";
    public const string BodyType = "bodyType";
    public const string MinYear = "minYear";
    public const string MaxYear = "maxYear";
    public const string MinMileage = "minMileage";
    public const string MaxMileage = "maxMileage";
    public const string Transmission = "transmission";
    public const string FuelType = "fuelType";
    public const string MinPrice = "minPrice";
    public const string MaxPrice = "maxPrice";

    /// <summary>
    /// What currency the budget was stated in.
    /// </summary>
    /// <remarks>
    /// Separate from the figure because a budget in rupees is not a budget in the base currency,
    /// and converting it at a guessed rate is exactly what decision D6 refuses to do for a
    /// listing. The operator sees "3,500,000 PKR" and decides; nothing writes a converted number
    /// into the requirement on their behalf.
    /// </remarks>
    public const string PriceCurrency = "priceCurrency";

    public const string DestinationCountryCode = "destinationCountryCode";

    public static readonly string[] All =
    [
        Make, Model, Variant, BodyType, MinYear, MaxYear, MinMileage, MaxMileage,
        Transmission, FuelType, MinPrice, MaxPrice, PriceCurrency, DestinationCountryCode,
    ];

    /// <summary>Fields holding a whole number.</summary>
    public static readonly string[] Whole =
    [
        MinYear, MaxYear, MinMileage, MaxMileage,
    ];

    /// <summary>Fields holding an amount of money.</summary>
    public static readonly string[] Money = [MinPrice, MaxPrice];
}

/// <summary>The outcome of asking a provider to read a message.</summary>
/// <remarks>
/// A result rather than an exception, for the same reason ranking uses one: every failure has the
/// same answer - tell the operator and let them type it themselves - and nothing here is
/// exceptional enough to unwind a call stack over.
/// </remarks>
public sealed record AIExtractionResult
{
    public bool Succeeded => Fields is not null;

    /// <summary>What was found, or null when the call failed or was refused.</summary>
    public IReadOnlyList<ExtractedField>? Fields { get; init; }

    /// <summary>Why there is nothing, in words the operator can act on.</summary>
    public string? Failure { get; init; }

    public AIUsage? Usage { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public static AIExtractionResult Failed(
        string why, string? provider = null, string? model = null)
        => new() { Failure = why, Provider = provider, Model = model };
}
