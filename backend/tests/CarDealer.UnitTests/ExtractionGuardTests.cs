using CarDealer.Application.AI;

namespace CarDealer.UnitTests;

/// <summary>
/// The ways a plausible-looking extraction can be false.
/// </summary>
/// <remarks>
/// Worse than a bad ranking, and the reason these are stricter. A wrong order is visible on the
/// screen it appears on; a budget the customer never named becomes part of their record, and
/// every search and alert afterwards answers a question nobody asked.
/// </remarks>
public sealed class ExtractionGuardTests
{
    private const string Message = """
        [name] here. Corolla Axio chahiye 2017 ya newer, under 35 lakh,
        mileage 100,000 se kam. Karachi port tak shipping.
        """;

    private static ExtractionRequest Request()
        => ExtractionRequest.From(new RedactedText(Message, 1));

    private static ExtractedField Field(string name, string value, string evidence)
        => new() { Field = name, Value = value, Evidence = evidence };

    [Fact]
    public void A_sound_extraction_passes()
    {
        // The anti-vacuity case. Without it every test below could pass by rejecting everything.
        var rejection = ExtractionGuards.Reject(Request(),
        [
            Field(ExtractionFields.Model, "Corolla Axio", "Corolla Axio chahiye"),
            Field(ExtractionFields.MinYear, "2017", "2017 ya newer"),
            Field(ExtractionFields.MaxPrice, "3500000", "under 35 lakh"),
            Field(ExtractionFields.PriceCurrency, "PKR", "35 lakh"),
            Field(ExtractionFields.MaxMileage, "100000", "mileage 100,000 se kam"),
        ]);

        Assert.Null(rejection);
    }

    [Fact]
    public void A_conversion_passes_while_its_evidence_stays_as_written()
    {
        // The reason evidence exists rather than grounding the figure itself. "35 lakh" is
        // 3,500,000 and neither string contains the other - grounding the number would reject
        // the one answer the prompt explicitly asks for.
        Assert.Null(ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.MaxPrice, "3500000", "35 lakh")]));
    }

    [Fact]
    public void A_budget_the_customer_never_named_is_refused()
    {
        // The failure this whole design exists to catch. The figure is plausible, the field is
        // real, and the customer said no such thing.
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.MinPrice, "2000000", "budget 20 lakh se start")]);

        Assert.Contains("not in the message", rejection);
    }

    [Fact]
    public void Evidence_that_paraphrases_is_refused()
    {
        // Close enough to read as honest, and it breaks the only check there is. If paraphrase
        // passed, invention would only need to sound like the message.
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.MinYear, "2017", "wants a 2017 or newer car")]);

        Assert.Contains("not in the message", rejection);
    }

    [Fact]
    public void Evidence_reflowed_onto_one_line_still_passes()
    {
        // Models rewrap text. Rejecting an honest quotation over a line break would fail it for
        // a reason unrelated to whether the customer said it.
        Assert.Null(ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.MaxMileage, "100000", "under 35 lakh, mileage 100,000 se kam")]));
    }

    [Fact]
    public void A_field_the_requirement_has_no_room_for_is_refused()
    {
        var rejection = ExtractionGuards.Reject(Request(),
            [Field("customerName", "Imran", "[name] here")]);

        Assert.Contains("not a field of a requirement", rejection);
    }

    [Fact]
    public void Two_values_for_one_field_are_refused()
    {
        var rejection = ExtractionGuards.Reject(Request(),
        [
            Field(ExtractionFields.MinYear, "2017", "2017 ya newer"),
            Field(ExtractionFields.MinYear, "2018", "2017 ya newer"),
        ]);

        Assert.Contains("two values", rejection);
    }

    [Fact]
    public void A_removed_detail_reported_as_an_answer_is_refused()
    {
        // Nothing leaks here - the placeholder is what redaction left. But "[name]" is not a
        // make, and offering it to the operator as one is the model reporting its own blindfold.
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.Make, "[name]", "[name] here")]);

        Assert.Contains("removed detail", rejection);
    }

    [Theory]
    [InlineData("20177")]
    [InlineData("217")]
    [InlineData("nineteen")]
    public void A_year_that_is_not_a_year_is_refused(string value)
    {
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.MinYear, value, "2017 ya newer")]);

        Assert.NotNull(rejection);
    }

    [Fact]
    public void A_mileage_no_odometer_reaches_is_refused()
    {
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.MaxMileage, "100000000", "mileage 100,000 se kam")]);

        Assert.Contains("not a mileage", rejection);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-500")]
    [InlineData("cheap")]
    public void A_price_that_is_not_a_price_is_refused(string value)
    {
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.MaxPrice, value, "under 35 lakh")]);

        Assert.NotNull(rejection);
    }

    [Fact]
    public void A_currency_that_is_not_a_code_is_refused()
    {
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.PriceCurrency, "Pakistani rupees", "35 lakh")]);

        Assert.Contains("not a code", rejection);
    }

    [Fact]
    public void A_destination_that_is_not_a_country_code_is_refused()
    {
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.DestinationCountryCode, "Karachi", "Karachi port")]);

        Assert.Contains("two-letter country code", rejection);
    }

    [Fact]
    public void An_empty_value_is_refused()
    {
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.Make, "  ", "Corolla Axio chahiye")]);

        Assert.Contains("no value", rejection);
    }

    [Fact]
    public void Missing_evidence_is_refused()
    {
        var rejection = ExtractionGuards.Reject(Request(),
            [Field(ExtractionFields.MinYear, "2017", "")]);

        Assert.Contains("no evidence", rejection);
    }

    [Fact]
    public void Finding_nothing_is_a_valid_answer()
    {
        // A message that states no requirement must come back empty rather than be forced to
        // produce something. Rule 1 of the prompt, checked rather than hoped for.
        Assert.Null(ExtractionGuards.Reject(Request(), []));
    }
}
