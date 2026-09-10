namespace CarDealer.Application.Messaging;

/// <summary>One template a new tenant starts with.</summary>
public sealed record StarterTemplate(string Name, string Body, int SortOrder);

/// <summary>
/// The templates a dealer is given on day one, so the feature is useful before they write
/// anything.
/// </summary>
/// <remarks>
/// <para>
/// Written to be replaced. These are a shape to edit rather than house style - the wording that
/// works is the dealer's own, in the register they already use with their customers, and a
/// broker in Karachi does not open a message the way this does. What they are really
/// demonstrating is the placeholder syntax and the line-drop rule, which are hard to describe
/// and obvious once seen working.
/// </para>
///
/// <para>
/// Two things are deliberately absent from every one of them. No template quotes commercial
/// terms - whether a price is FOB or CIF, what freight is excluded, how long a quote stands -
/// because those are the dealer's terms and inventing them would put words in their mouth that
/// a customer could hold them to. And none carries <c>{ListingUrl}</c>: it is available to
/// anyone who wants it, per the operator's decision, but a starter that shipped with the
/// exporter's link already in it would give away the dealer's margin by default rather than by
/// choice.
/// </para>
/// </remarks>
public static class StarterTemplates
{
    public static IReadOnlyList<StarterTemplate> All { get; } =
    [
        new("New car offer", """
            Hi {FirstName|there},

            I have a {Vehicle|car} that might suit you:

            • Year: {Year}
            • Mileage: {Mileage}
            • Colour: {Colour}
            • Fuel: {Fuel}
            • Transmission: {Transmission}
            • Steering: {Steering}

            Happy to answer any questions.
            {DealerName}
            """, 10),

        new("Price quote", """
            Hi {FirstName|there},

            Here is my price for the {Vehicle|car}:

            • Year: {Year}
            • Mileage: {Mileage}
            • Colour: {Colour}

            Price: {Price}

            Let me know if you would like me to hold it for you.
            {DealerName}
            """, 20),

        new("Follow-up", """
            Hi {FirstName|there},

            Just following up on the {Vehicle|car} I sent you. Is it still of interest, or would you like me to keep looking?

            {DealerName}
            """, 30),

        new("Car no longer available", """
            Hi {FirstName|there},

            The {Vehicle|car} I sent you has gone, so I have taken it off your list. I will keep looking and send you the next one that fits.

            {DealerName}
            """, 40),

        new("Plain message", """
            Hi {FirstName|there},

            {DealerName}
            """, 50),
    ];
}
