using System.Globalization;
using System.Text;
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
/// What goes in is what a buyer asks in their reply anyway - year, mileage, drivetrain, the
/// price with its incoterm, and a link to the source listing so they can see the photos. Two
/// things stay out on purpose: anything the platform is not sure of, and the flourish. A
/// message that reads as though a machine wrote it gets answered like one.
/// </para>
/// </remarks>
public static class MessageComposer
{
    public static string ForVehicle(Customer customer, Vehicle vehicle, VehicleListing? listing, string tenantName)
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
            vehicle.FuelType == FuelType.Unknown ? null : vehicle.FuelType.ToString(),
            vehicle.Transmission == Transmission.Unknown ? null : vehicle.Transmission.ToString(),
            vehicle.SteeringSide switch
            {
                SteeringSide.RightHandDrive => "RHD",
                SteeringSide.LeftHandDrive => "LHD",
                _ => null,
            },
        }.Where(s => s is not null);

        if (specs.Any())
        {
            text.Append("\n• ").Append(string.Join(" · ", specs));
        }

        if (listing?.Price is { } price)
        {
            text.Append("\n• Price: ")
                .Append(price.ToString("N0", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(listing.CurrencyCode ?? string.Empty);

            // The incoterm travels with the price or the number means nothing: an FOB figure
            // and a CIF figure are not the same offer, and a buyer comparing quotes needs to
            // know which one this is.
            if (listing.PriceType != PriceType.Unknown)
            {
                text.Append(" (").Append(Incoterm(listing.PriceType)).Append(')');
            }
        }

        if (!string.IsNullOrWhiteSpace(listing?.SourceUrl))
        {
            text.Append("\n\nPhotos and full details: ").Append(listing.SourceUrl);
        }

        text.Append("\n\nHappy to send more photos or answer anything.\n").Append(tenantName);

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

    private static string Incoterm(PriceType type) => type switch
    {
        PriceType.ExWorks => "EXW",
        PriceType.FreeOnBoard => "FOB",
        PriceType.CostAndFreight => "CFR",
        PriceType.CostInsuranceFreight => "CIF",
        _ => string.Empty,
    };
}
