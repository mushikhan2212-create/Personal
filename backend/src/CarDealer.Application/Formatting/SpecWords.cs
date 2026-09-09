using CarDealer.Domain.Enums;

namespace CarDealer.Application.Formatting;

/// <summary>
/// Catalogue enum values as the trade writes them, for text a person reads.
/// </summary>
/// <remarks>
/// <para>
/// The API sends enums as names rather than numbers, so that renumbering on the server cannot
/// silently change what a filter means. The cost is that the name is a C# identifier, and any
/// prose composed on this side and rendered verbatim shows it: "ContinuouslyVariable" where the
/// trade says CVT, "RightHandDrive" where it says RHD.
/// </para>
///
/// <para>
/// One place rather than one per feature, because the alternative was tried. The spelling lived
/// inline in the WhatsApp composer, then again in the requirement explanation, and when the
/// duplicate scorer came to write its reasons it grew a third copy that promptly said
/// "both RightHandDrive" on a review screen. The browser has its own table for the values it
/// renders itself; this is for the strings the server writes out as sentences, which arrive with
/// nothing left to translate.
/// </para>
///
/// <para>
/// Members whose name is already the word are absent on purpose. "Petrol" and "Automatic" need
/// no translation, and listing them would only invite this table to drift from the enum.
/// </para>
///
/// <para>
/// Capitalised as a standalone label, because that is what every caller needs: a bullet in a
/// message reads "Plug-in hybrid", and the requirement explanation reads "fuel Plug-in hybrid"
/// beside "make Toyota" and "body Sedan", which are capitalised for the same reason. Two casings
/// would mean two tables, and two tables is how the third copy of this got written.
/// </para>
/// </remarks>
public static class SpecWords
{
    public static string Of(Transmission value) => value switch
    {
        Transmission.ContinuouslyVariable => "CVT",
        Transmission.SemiAutomatic => "Semi-automatic",
        Transmission.DualClutch => "Dual clutch",
        _ => value.ToString(),
    };

    public static string Of(FuelType value) => value switch
    {
        FuelType.PluginHybrid => "Plug-in hybrid",
        FuelType.Lpg => "LPG",
        FuelType.Cng => "CNG",
        _ => value.ToString(),
    };

    public static string Of(SteeringSide value) => value switch
    {
        SteeringSide.RightHandDrive => "RHD",
        SteeringSide.LeftHandDrive => "LHD",
        _ => value.ToString(),
    };

    public static string Of(Drivetrain value) => value switch
    {
        Drivetrain.FrontWheelDrive => "FWD",
        Drivetrain.RearWheelDrive => "RWD",
        Drivetrain.AllWheelDrive => "AWD",
        Drivetrain.FourWheelDrive => "4WD",
        _ => value.ToString(),
    };
}
