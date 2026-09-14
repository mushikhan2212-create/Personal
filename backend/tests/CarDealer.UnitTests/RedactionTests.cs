using CarDealer.Application.AI;

namespace CarDealer.UnitTests;

/// <summary>
/// What must not leave the building, and what must survive so the feature still works.
/// </summary>
/// <remarks>
/// Both halves matter and they pull against each other. A redactor that removes everything is
/// perfectly safe and perfectly useless: the numbers a customer states about a car are the whole
/// point of reading their message. So every test here is either "this identifier is gone" or
/// "this car fact is still there", and the pair is the specification.
/// </remarks>
public sealed class RedactionTests
{
    /// <summary>A real enquiry, in the language and shape these actually arrive in.</summary>
    private const string Enquiry = """
        Asalam o alaikum, main Imran Sheikh, Lahore se. Mera number 0300-1234567 hai aur
        email imran.sheikh@gmail.com. Corolla Axio chahiye 2017 ya newer, under 35 lakh,
        mileage 100,000 se kam. Karachi port tak shipping. CNIC 35202-1234567-8.
        """;

    private static string Clean(string text, params string[] names)
        => Redaction.Apply(text, names).Text;

    [Fact]
    public void The_whole_enquiry_loses_every_identifier()
    {
        var clean = Clean(Enquiry, "Imran", "Sheikh");

        Assert.DoesNotContain("0300-1234567", clean);
        Assert.DoesNotContain("imran.sheikh@gmail.com", clean);
        Assert.DoesNotContain("35202-1234567-8", clean);
        Assert.DoesNotContain("Imran", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sheikh", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_whole_enquiry_keeps_every_car_fact()
    {
        // The other half. These five are what the operator is trying to avoid typing, and a
        // redactor that takes any of them has made the feature pointless while looking safe.
        var clean = Clean(Enquiry, "Imran", "Sheikh");

        Assert.Contains("Corolla Axio", clean);
        Assert.Contains("2017", clean);
        Assert.Contains("35 lakh", clean);
        Assert.Contains("100,000", clean);
        Assert.Contains("Karachi", clean);
    }

    [Theory]
    [InlineData("0300-1234567")]
    [InlineData("0300 1234567")]
    [InlineData("03001234567")]
    [InlineData("+92 300 1234567")]
    [InlineData("+923001234567")]
    [InlineData("+92-300-1234567")]
    [InlineData("042-35712345")]
    [InlineData("923001234567")]
    public void Every_way_a_number_gets_written_here(string number)
    {
        var clean = Clean($"call me on {number} please");

        Assert.DoesNotContain(number, clean);
        Assert.Contains("[phone]", clean);
    }

    [Fact]
    public void A_number_does_not_swallow_what_follows_it()
    {
        // The bug this pattern was written around. An open-ended run of digits and spaces eats
        // the year after the phone number, and the extraction silently loses a field nobody can
        // see was ever there.
        var clean = Clean("+92 300 1234567 aur 2017 model chahiye");

        Assert.Contains("2017", clean);
        Assert.Contains("model chahiye", clean);
    }

    [Theory]
    [InlineData("35202-1234567-8")]
    [InlineData("3520212345678")]
    public void A_national_id_goes_in_either_spelling(string cnic)
    {
        var clean = Clean($"CNIC {cnic}");

        Assert.DoesNotContain(cnic, clean);
        Assert.Contains("[id]", clean);
    }

    [Fact]
    public void A_chat_link_goes_whole()
    {
        // Half-redacting this would leave "wa.me/" standing, which reports the channel and the
        // fact of a number while claiming the number was removed.
        var clean = Clean("message me https://wa.me/923001234567");

        Assert.DoesNotContain("923001234567", clean);
        Assert.DoesNotContain("wa.me", clean);
    }

    [Theory]
    [InlineData("2017")]
    [InlineData("100000")]
    [InlineData("3500000")]
    [InlineData("50000000")]
    public void Numbers_this_trade_actually_writes_are_left_alone(string figure)
    {
        // A year, a mileage, a budget, and the price of a car nobody here sells. None of them is
        // long enough to be an identifier, and taking any of them breaks the extraction.
        Assert.Contains(figure, Clean($"budget {figure} hai"));
    }

    [Fact]
    public void A_name_is_removed_wherever_it_sits_and_in_any_case()
    {
        var clean = Clean("IMRAN here, my friend imran also wants one", "Imran Sheikh");

        Assert.DoesNotContain("IMRAN", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[name]", clean);
    }

    [Fact]
    public void A_name_is_not_removed_from_inside_another_word()
    {
        // Word boundaries, not substrings. Without them a customer named Ali takes "quality",
        // "Alison" and half the message with him.
        var clean = Clean("quality car chahiye, Ali", "Ali");

        Assert.Contains("quality", clean);
        Assert.DoesNotContain(" Ali", clean);
    }

    [Fact]
    public void Short_name_tokens_are_left_alone()
    {
        // Initials and particles collide with ordinary words. "M" would redact every stray M in
        // the message and leave the operator reading confetti.
        var clean = Clean("M A Khan wants a car", "M A Khan");

        Assert.Contains("M A", clean);
        Assert.DoesNotContain("Khan", clean);
    }

    [Fact]
    public void It_counts_what_it_took()
    {
        // Surfaced to the operator, so "3 details removed before sending" is a statement they
        // can check against the message rather than a promise they have to trust.
        var result = Redaction.Apply(
            "Imran, 0300-1234567, imran@x.com, CNIC 35202-1234567-8", ["Imran"]);

        Assert.Equal(4, result.Removed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_nothing_out(string? text)
    {
        var result = Redaction.Apply(text, ["Imran"]);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void A_message_with_nothing_to_hide_is_untouched()
    {
        // The anti-vacuity case: without it every test above could pass by returning "[phone]".
        const string plain = "Corolla Axio 2017, under 35 lakh, 100,000 km se kam";

        Assert.Equal(plain, Clean(plain, "Imran"));
    }

    [Theory]
    [InlineData("[phone]")]
    [InlineData("Toyota [name]")]
    [InlineData("[ID]")]
    public void A_placeholder_coming_back_in_a_field_is_detectable(string value)
        => Assert.True(Redaction.ContainsPlaceholder(value));

    [Theory]
    [InlineData("Toyota")]
    [InlineData(null)]
    [InlineData("")]
    public void An_ordinary_value_is_not_mistaken_for_one(string? value)
        => Assert.False(Redaction.ContainsPlaceholder(value));
}
