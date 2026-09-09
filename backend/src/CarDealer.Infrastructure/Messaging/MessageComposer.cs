using System.Globalization;
using System.Text;
using CarDealer.Application.Formatting;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;

namespace CarDealer.Infrastructure.Messaging;

/// <summary>
/// Writes the first draft of a message about a car.
/// </summary>
/// <remarks>
/// A draft, not a template in the WhatsApp sense - Meta-approved <c>MessageTemplates</c> are a
/// Business API concern and a deferred table. This is the text that appears in the compose box
/// for a salesperson to edit before sending, and editing it is the point: master prompt section
/// 18 excludes autonomous customer messaging, and a message a person cannot change before it
/// goes is closer to autonomous than to assisted.
///
/// <para>
/// What goes in is the car itself: year, mileage, drivetrain. What stays out is the price and
/// the source listing link - see the note in <c>ForVehicle</c>, which is about margin rather
/// than brevity. Also out: anything the platform is not sure of, and the flourish. A message
/// that reads as though a machine wrote it gets answered like one.
/// </para>
/// </remarks>
public static class MessageComposer
{
    public static string ForVehicle(Customer customer, Vehicle vehicle, string tenantName)
    {
        var text = new StringBuilder();

        // First name only. "Dear Imran Sheikh" is how a bank writes; a dealer who has the
        // customer's mobile number is on first-name terms with them.
        text.Append(string.IsNullOrWhiteSpace(customer.FirstName)
            ? "Hello,"
            : $"Hi {customer.FirstName.Trim()},");

        var name = Describe(vehicle);

        text.Append("\n\nI have a ").Append(name).Append(" that might suit you:\n");

        if (vehicle.ModelYear is { } year)
        {
            text.Append("\n• Year: ").Append(year.ToString(CultureInfo.InvariantCulture));
        }

        if (vehicle.Mileage is { } mileage)
        {
            text.Append("\n• Mileage: ")
                .Append(mileage.ToString("N0", CultureInfo.InvariantCulture))
                .Append(vehicle.MileageUnit == MileageUnit.Miles ? " mi" : " km");
        }

        var specs = new[]
        {
            // Through SpecWords. Left as ToString these read "ContinuouslyVariable" - a C#
            // identifier, in a message a customer opens on their phone, which is the one place
            // an enum name must never surface.
            vehicle.FuelType == FuelType.Unknown ? null : SpecWords.Of(vehicle.FuelType),
            vehicle.Transmission == Transmission.Unknown
                ? null
                : SpecWords.Of(vehicle.Transmission),
            vehicle.SteeringSide == SteeringSide.Unknown
                ? null
                : SpecWords.Of(vehicle.SteeringSide),
        }.Where(s => s is not null);

        if (specs.Any())
        {
            text.Append("\n• ").Append(string.Join(" · ", specs));
        }

        // Neither the price nor the source listing link goes in.
        //
        // The link is the important one: this dealer brokers other exporters' stock, and the
        // URL names the exporter. A customer who follows it can buy direct, which is the
        // dealer's margin walking out of the door. The price is left out for the same reason
        // in a softer form - a quote is a conversation, and a number in an opening message
        // invites a haggle before anyone has established the car is right.
        //
        // Photos are attached by the salesperson in WhatsApp rather than linked: a click-to-chat
        // link carries text only, and the image URL would name the exporter exactly as the
        // listing link does.
        text.Append("\n\nHappy to answer any questions.\n").Append(tenantName);

        return text.ToString();
    }

    /// <summary>The opener for a message with no particular car attached.</summary>
    public static string ForCustomer(Customer customer, string tenantName)
        => (string.IsNullOrWhiteSpace(customer.FirstName)
                ? "Hello,"
                : $"Hi {customer.FirstName.Trim()},")
            + $"\n\n\n{tenantName}";

    private static string Describe(Vehicle vehicle)
    {
        var parts = new[] { vehicle.Make, vehicle.Model, vehicle.Variant }
            .Where(p => !string.IsNullOrWhiteSpace(p));

        var name = string.Join(' ', parts);

        return string.IsNullOrWhiteSpace(name) ? "vehicle" : name;
    }
}
