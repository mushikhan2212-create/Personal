using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Messaging;

namespace CarDealer.UnitTests;

/// <summary>
/// The text of the first draft a salesperson sees.
/// </summary>
/// <remarks>
/// Two things are being protected here, and neither is a matter of taste.
///
/// <para>
/// The first is margin. This dealer brokers other exporters' stock, so the source listing link
/// names a supplier the customer can buy from directly, and the price invites a haggle before
/// anyone has agreed the car is right. Both were deliberately left out, and a test is the only
/// thing that stops them being added back by someone who reasonably assumes a car message
/// should quote a price.
/// </para>
///
/// <para>
/// The second is that the message is read by a customer, on their phone, in a chat. An enum
/// name reaching it - "ContinuouslyVariable" where the trade says CVT - is not a cosmetic slip
/// there; it is the platform's internals showing through to somebody buying a car.
/// </para>
/// </remarks>
public sealed class MessageComposerTests
{
    private static Customer ACustomer(string? firstName = "Imran") => new()
    {
        FirstName = firstName,
        LastName = "Sheikh",
        Phone = "+923001234567",
    };

    private static Vehicle AVehicle(
        Transmission transmission = Transmission.ContinuouslyVariable,
        FuelType fuel = FuelType.Petrol) => new()
    {
        Make = "Toyota",
        Model = "Corolla Axio",
        Variant = "G",
        ModelYear = 2018,
        Mileage = 101_828,
        MileageUnit = MileageUnit.Kilometers,
        FuelType = fuel,
        Transmission = transmission,
        SteeringSide = SteeringSide.RightHandDrive,
    };

    [Theory]
    [InlineData(Transmission.ContinuouslyVariable, "CVT")]
    [InlineData(Transmission.SemiAutomatic, "Semi-automatic")]
    [InlineData(Transmission.DualClutch, "Dual clutch")]
    public void A_multi_word_transmission_reads_as_the_trade_writes_it(
        Transmission transmission, string expected)
    {
        var text = MessageComposer.ForVehicle(
            ACustomer(), AVehicle(transmission), "Nihon Motors");

        Assert.Contains(expected, text, StringComparison.Ordinal);

        // The enum name itself must not survive anywhere in the message.
        Assert.DoesNotContain(transmission.ToString(), text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FuelType.PluginHybrid, "Plug-in hybrid")]
    [InlineData(FuelType.Lpg, "LPG")]
    [InlineData(FuelType.Cng, "CNG")]
    public void A_fuel_type_whose_enum_name_is_not_a_word_is_translated(
        FuelType fuel, string expected)
    {
        var text = MessageComposer.ForVehicle(
            ACustomer(), AVehicle(fuel: fuel), "Nihon Motors");

        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain(fuel.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fuel_type_that_is_already_a_word_is_left_alone()
    {
        var text = MessageComposer.ForVehicle(
            ACustomer(), AVehicle(fuel: FuelType.Diesel), "Nihon Motors");

        Assert.Contains("Diesel", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_spec_is_omitted_rather_than_named_Unknown()
    {
        var text = MessageComposer.ForVehicle(
            ACustomer(),
            AVehicle(Transmission.Unknown, FuelType.Unknown),
            "Nihon Motors");

        Assert.DoesNotContain("Unknown", text, StringComparison.Ordinal);

        // The one spec that is known still appears, so omitting the others has not
        // swallowed the line they share.
        Assert.Contains("RHD", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_carries_the_car_the_year_and_the_mileage()
    {
        var text = MessageComposer.ForVehicle(ACustomer(), AVehicle(), "Nihon Motors");

        Assert.Contains("Toyota Corolla Axio G", text, StringComparison.Ordinal);
        Assert.Contains("2018", text, StringComparison.Ordinal);
        Assert.Contains("101,828 km", text, StringComparison.Ordinal);
        Assert.Contains("Hi Imran,", text, StringComparison.Ordinal);
        Assert.Contains("Nihon Motors", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_customer_with_no_first_name_is_greeted_without_one()
    {
        var text = MessageComposer.ForVehicle(ACustomer(null), AVehicle(), "Nihon Motors");

        Assert.StartsWith("Hello,", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sheikh", text, StringComparison.Ordinal);
    }
}
