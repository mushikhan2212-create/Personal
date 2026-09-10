using System.Globalization;
using System.Text.RegularExpressions;
using CarDealer.Application.Formatting;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;

namespace CarDealer.Application.Messaging;

/// <summary>
/// Reads the values a template's placeholders stand for off the real records.
/// </summary>
/// <remarks>
/// Separate from <see cref="TemplateRenderer"/> so the rendering rules can be tested on a plain
/// dictionary without building a catalogue, and so the one commercially dangerous decision in
/// this feature - where <c>{Price}</c> comes from - sits by itself where it can be read.
/// </remarks>
public static partial class TemplateFields
{
    /// <summary>
    /// Builds the value set for one customer and, optionally, one car.
    /// </summary>
    /// <param name="customer">Who the message is to.</param>
    /// <param name="vehicle">The car it is about, or null for a message with none.</param>
    /// <param name="overlay">
    /// This tenant's commercial state over <paramref name="vehicle"/>, which is where the price
    /// comes from.
    /// </param>
    /// <param name="dealerName">The tenant's own name, for the sign-off.</param>
    public static Dictionary<string, string?> For(
        Customer customer,
        Vehicle? vehicle,
        TenantVehicle? overlay,
        string dealerName)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = customer.FirstName?.Trim(),
            ["LastName"] = customer.LastName?.Trim(),
            ["City"] = customer.City?.Trim(),
            ["Country"] = customer.CountryCode?.Trim(),
            ["DealerName"] = dealerName,
        };

        if (vehicle is null)
        {
            return values;
        }

        values["Make"] = Clean(vehicle.Make);
        values["Model"] = Clean(vehicle.Model);
        values["Variant"] = Clean(vehicle.Variant);
        values["Vehicle"] = Describe(vehicle);
        values["Year"] = vehicle.ModelYear?.ToString(CultureInfo.InvariantCulture);
        values["Colour"] = vehicle.ExteriorColor;
        values["BodyType"] = vehicle.BodyType;

        values["Mileage"] = vehicle.Mileage is { } mileage
            ? mileage.ToString("N0", CultureInfo.InvariantCulture)
                + (vehicle.MileageUnit == MileageUnit.Miles ? " mi" : " km")
            : null;

        values["Engine"] = vehicle.EngineDisplacementCc is { } cc
            ? cc.ToString("N0", CultureInfo.InvariantCulture) + "cc"
            : null;

        // Through SpecWords, never ToString. An enum left to itself renders "ContinuouslyVariable"
        // or "RightHandDrive" - a C# identifier, in a message a customer opens on their phone.
        values["Fuel"] = Word(vehicle.FuelType, FuelType.Unknown, SpecWords.Of);
        values["Transmission"] = Word(vehicle.Transmission, Transmission.Unknown, SpecWords.Of);
        values["Steering"] = Word(vehicle.SteeringSide, SteeringSide.Unknown, SpecWords.Of);
        values["Drivetrain"] = Word(vehicle.Drivetrain, Drivetrain.Unknown, SpecWords.Of);

        // The price is the tenant's own retail price and nothing else.
        //
        // This is the whole reason TenantVehicle exists (decision D1): the catalogue rows carry
        // what the *exporter* is asking, and this dealer brokers other exporters' stock. Resolving
        // {Price} from a source listing would put the dealer's own buying price in front of the
        // customer - their entire margin, quoted to the person they are quoting to. The overlay
        // price is the only number here that the dealer chose to sell at.
        //
        // Unset means empty, which drops the line rather than falling back to anything. A quote
        // template that silently renders no price is a template somebody notices; one that
        // renders the wrong price is not.
        values["Price"] = overlay?.TenantPrice is { } price
            ? $"{overlay.TenantCurrencyCode ?? "USD"} {price.ToString("N0", CultureInfo.InvariantCulture)}"
            : null;

        // Deliberately the *active* listing's URL, and deliberately available at all only
        // because the operator asked for it per-template. It names the exporter, and a customer
        // who follows it can buy direct - see the warning the template editor shows.
        values["ListingUrl"] = vehicle.Listings
            .Where(l => l.IsActive && !string.IsNullOrWhiteSpace(l.SourceUrl))
            .OrderByDescending(l => l.LastSeenAtUtc)
            .Select(l => l.SourceUrl)
            .FirstOrDefault();

        return values;
    }

    private static string? Word<T>(T value, T unknown, Func<T, string> word)
        where T : struct, Enum
        => EqualityComparer<T>.Default.Equals(value, unknown) ? null : word(value);

    /// <summary>
    /// Collapses the whitespace a scraped field arrives with.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. <c>Variant</c> on this catalogue is a fragment of the exporter's own listing
    /// title - real values include "2017&#160;&#160;&#160;1.6 CVT PUSHSTART NAVI REVCAM" - and a
    /// run of spaces that goes unnoticed in a table cell reads as a typo in the middle of a
    /// sentence somebody sends to a customer. Internal runs collapse to one space; the words
    /// themselves are left exactly as the source wrote them, because correcting a dealer's model
    /// naming is not this method's business.
    /// </remarks>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return WhitespaceRun().Replace(value.Trim(), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    private static string? Describe(Vehicle vehicle)
    {
        var name = string.Join(' ', new[] { vehicle.Make, vehicle.Model, vehicle.Variant }
            .Select(Clean)
            .Where(p => p is not null));

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
