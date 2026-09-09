using CarDealer.Application.Messaging;

namespace CarDealer.UnitTests;

/// <summary>
/// Resolving a stored phone number to the international form a messaging link needs.
/// </summary>
/// <remarks>
/// The failure this guards against is not an error message - it is a link that opens a chat
/// with a real person who is not the customer, because a country was guessed. So the refusal
/// cases below matter more than the success cases, and each of them is a number a dealer's
/// contact list actually contains.
/// </remarks>
public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("+92 300 1234567", "923001234567")]
    [InlineData("+92-300-1234567", "923001234567")]
    [InlineData("+923001234567", "923001234567")]
    [InlineData("(+92) 300 1234567", "923001234567")]
    [InlineData("+81 90 1234 5678", "819012345678")]
    public void A_number_that_carries_its_own_country_needs_nothing_from_us(
        string stored, string expected)
    {
        // No country argument at all: the number says where it is going.
        var result = PhoneNumber.Resolve(stored, null);

        Assert.True(result.IsResolved);
        Assert.Equal(expected, result.Digits);
    }

    [Fact]
    public void A_double_zero_exit_prefix_is_the_same_as_a_plus()
    {
        var result = PhoneNumber.Resolve("0092 300 1234567", null);

        Assert.Equal("923001234567", result.Digits);
    }

    [Theory]
    [InlineData("0300 1234567", "PK", "923001234567")]
    [InlineData("03001234567", "PK", "923001234567")]
    [InlineData("090-1234-5678", "JP", "819012345678")]
    [InlineData("0712 345678", "KE", "254712345678")]
    public void A_national_number_is_resolved_from_the_customers_country(
        string stored, string country, string expected)
    {
        // The trunk zero is dropped when the country code goes on: 0300… in Pakistan is
        // +92 300…, not +92 0300…
        var result = PhoneNumber.Resolve(stored, country);

        Assert.Equal(expected, result.Digits);
    }

    [Fact]
    public void A_national_number_with_no_country_is_refused_rather_than_guessed()
    {
        // The case the whole file exists for. "0300 1234567" is a real number in a dozen
        // countries; picking one would open a chat with a stranger.
        var result = PhoneNumber.Resolve("0300 1234567", null);

        Assert.False(result.IsResolved);
        Assert.Contains("no country code", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_country_we_have_no_dialling_code_for_is_refused()
    {
        var result = PhoneNumber.Resolve("0300 1234567", "ZZ");

        Assert.False(result.IsResolved);
        Assert.Contains("ZZ", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_already_carrying_its_country_code_is_not_prefixed_twice()
    {
        // Typed without the plus. Prefixing again gives 9292…, which is a number in nobody's
        // country and a link that silently goes nowhere.
        var result = PhoneNumber.Resolve("923001234567", "PK");

        Assert.Equal("923001234567", result.Digits);
    }

    [Fact]
    public void A_missing_number_is_refused_with_a_reason_a_salesperson_can_act_on()
    {
        foreach (var empty in new[] { null, "", "   " })
        {
            var result = PhoneNumber.Resolve(empty, "PK");

            Assert.False(result.IsResolved);
            Assert.Contains("no phone number", result.Reason!, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("---")]
    public void Text_with_no_digits_is_refused(string stored)
    {
        var result = PhoneNumber.Resolve(stored, "PK");

        Assert.False(result.IsResolved);
    }

    [Theory]
    [InlineData("+92 300 12")]
    [InlineData("+9999999999999999999")]
    public void A_number_of_implausible_length_is_refused(string stored)
    {
        // E.164 tops out at 15 digits, and nothing under 8 is a mobile. Both ends produce a
        // link that fails in WhatsApp rather than in the app, where nobody can explain it.
        var result = PhoneNumber.Resolve(stored, "PK");

        Assert.False(result.IsResolved);
        Assert.Contains("international number", result.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("+92 300 1234567 ext 12")]
    [InlineData("+92 300 1234567 x12")]
    public void An_extension_is_refused_rather_than_truncated(string stored)
    {
        // A landline with an extension cannot receive WhatsApp at all, and quietly dropping
        // the extension would produce a link to the switchboard.
        var result = PhoneNumber.Resolve(stored, "PK");

        Assert.False(result.IsResolved);
        Assert.Contains("extension", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_country_is_matched_regardless_of_case()
    {
        Assert.Equal("923001234567", PhoneNumber.Resolve("0300 1234567", "pk").Digits);
    }
}
