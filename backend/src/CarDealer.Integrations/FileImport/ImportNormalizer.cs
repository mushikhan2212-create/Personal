using System.Text.Json;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;

namespace CarDealer.Integrations.FileImport;

/// <summary>
/// Turns an import record into canonical entities.
/// </summary>
/// <remarks>
/// Simpler than <c>CarapisNormalizer</c> on purpose. That one has to reverse-engineer a
/// vendor's decisions - inferring steering from prose, refusing a price_usd that is
/// demonstrably wrong, treating "" as absent. This format is ours, so it states things plainly
/// and the normalizer's job is to reject what is not stated rather than to guess.
///
/// The rules that do carry over are the ones that were learned the hard way:
/// CanonicalIdentity decides identity (blank is absent, never a hash), a missing incoterm
/// stays Unknown rather than being assumed, and absent availability is Unknown rather than
/// Unavailable.
/// </remarks>
public sealed class ImportNormalizer : IVehicleRecordNormalizer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public VehicleSourceProviderType ProviderType => VehicleSourceProviderType.DealerJson;

    public NormalizedVehicle? Normalize(
        RawVehicleRecord record, long vehicleSourceId, string? mediaBaseUrl = null)
    {
        var dto = JsonSerializer.Deserialize<VehicleImportRecord>(record.RawPayload, Json);

        if (dto is null)
        {
            return null;
        }

        // Freshness falls back to when the document was captured - RetrievedAtUtc, which the
        // provider took from the file's capturedAtUtc. Producers rarely stamp each row, and
        // the document already answers the question honestly: every record in it was seen when
        // it was made. What is never substituted is "now" at import time, because that would
        // assert a confirmation nobody made - the exact false freshness that made the previous
        // source unusable.
        var lastSeen = dto.LastSeenAtUtc ?? record.RetrievedAtUtc;

        var vin = CanonicalIdentity.Normalize(dto.Vin);
        var chassis = CanonicalIdentity.Normalize(dto.ChassisNumber);

        // The lot number is the source's own stock id. It identifies the car within this
        // source and nowhere else, which is exactly what CanonicalIdentity does with it -
        // hashing it together with the source id. ChassisCode is deliberately absent here:
        // it names a model, not a car, so two different Passos share one.
        var lot = CanonicalIdentity.Normalize(dto.LotNumber);
        var (hash, hashSource) = CanonicalIdentity.Build(vin, chassis, vehicleSourceId, lot);

        var inferred = new List<string>();

        var vehicle = new Vehicle
        {
            TenantId = null,
            PublicId = Guid.NewGuid(),
            Make = Blank(dto.Make),
            Model = Blank(dto.Model),
            Variant = Blank(dto.Variant) ?? VariantFromTitle(dto, inferred),
            ModelYear = dto.Year,
            BodyType = Blank(dto.BodyType),
            EngineDisplacementCc = dto.EngineCc,
            FuelType = ParseFuel(dto.FuelType),
            Transmission = ParseTransmission(dto.Transmission),
            Drivetrain = ParseDrivetrain(dto.Drivetrain),
            SteeringSide = ParseSteering(dto.Steering),
            Mileage = dto.Mileage,

            // A mileage with no unit is not comparable with one that has a unit, so the unit is
            // Unknown unless stated - never assumed to be kilometres because the stock is
            // Japanese.
            MileageUnit = ParseMileageUnit(dto.MileageUnit),

            ExteriorColor = Blank(dto.ExteriorColor),
            Doors = ToByte(dto.Doors),
            Seats = ToByte(dto.Seats),
            Condition = Blank(dto.ConditionNotes),

            // A model code, stored as the specification it is. Searchable and displayable,
            // and never fed to CanonicalIdentity.
            Engine = Blank(dto.ChassisCode),
            Vin = vin,
            ChassisNumber = chassis,
            LotNumber = lot,
            CanonicalHash = hash,
            CanonicalHashSource = hashSource,
            Status = dto.IsAvailable switch
            {
                true => VehicleStatus.Active,
                false => VehicleStatus.Unavailable,
                null => VehicleStatus.Unknown,
            },
        };

        var listing = new VehicleListing
        {
            TenantId = null,
            VehicleSourceId = vehicleSourceId,
            ExternalListingId = record.ExternalId,
            SourceUrl = Blank(dto.ListingUrl),
            Price = dto.Price,
            CurrencyCode = Blank(dto.Currency)?.ToUpperInvariant(),
            PriceType = ParsePriceType(dto.PriceType),

            // Left for the FX step to pin against a dated rate, per decision D6. An import
            // stating a price in JPY has not stated one in USD, and converting it here with
            // today's rate would invent a number with no rate id behind it.
            PriceBaseCurrency = null,
            ExchangeRateId = null,

            PortOfLoading = Blank(dto.PortOfLoading),
            LocationCountryCode = Blank(dto.LocationCountry)?.ToUpperInvariant(),
            LocationCity = Blank(dto.LocationCity),
            RawPayload = record.RawPayload,
            FirstSeenAtUtc = dto.FirstSeenAtUtc ?? lastSeen,
            LastSeenAtUtc = lastSeen,
            LastSyncedAtUtc = record.RetrievedAtUtc,
            IsActive = dto.IsAvailable ?? true,
        };

        var images = dto.ImageUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select((url, index) => new VehicleImage
            {
                TenantId = null,
                ImageUrl = url.Trim(),
                SortOrder = index,
            })
            .ToList();

        return new NormalizedVehicle
        {
            Vehicle = vehicle,
            Listing = listing,
            Images = images,

            // Almost nothing is inferred - the advantage of owning the format. The exception
            // is a variant read out of the title, which is recorded so a derived grade is never
            // mistaken for one the source stated.
            InferredFields = inferred,
        };
    }

    /// <summary>The destinations a record states, for the coverage filter.</summary>
    public static IReadOnlyCollection<string> DestinationsOf(RawVehicleRecord record)
    {
        var dto = JsonSerializer.Deserialize<VehicleImportRecord>(record.RawPayload, Json);

        return dto?.DestinationMarkets
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim())
            .ToList() ?? [];
    }

    private static byte? ToByte(int? value)
        => value is >= 0 and <= 255 ? (byte)value.Value : null;

    /// <summary>
    /// Pulls a grade out of the source's headline when no variant field was supplied.
    /// </summary>
    /// <remarks>
    /// BE FORWARD publishes no trim field but writes it into the title: "2022 TOYOTA PASSO
    /// 1.0XLPKG". Stripping the year, make and model leaves the grade. Nothing is invented -
    /// when what remains is empty, the variant stays null rather than becoming the whole title,
    /// which would put "2022 TOYOTA PASSO" in a field meant for "1.0XLPKG".
    /// </remarks>
    private static string? VariantFromTitle(VehicleImportRecord dto, List<string> inferred)
    {
        var title = Blank(dto.Title);

        if (title is null)
        {
            return null;
        }

        var remainder = title;

        foreach (var part in new[] { dto.Year?.ToString(), dto.Make, dto.Model })
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            var at = remainder.IndexOf(part, StringComparison.OrdinalIgnoreCase);

            if (at >= 0)
            {
                remainder = remainder.Remove(at, part.Length);
            }
        }

        remainder = remainder.Trim();

        if (remainder.Length == 0)
        {
            return null;
        }

        inferred.Add($"variant (read from title \"{title}\")");
        return remainder;
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SteeringSide ParseSteering(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "rhd" or "right" or "right-hand drive" or "righthanddrive" => SteeringSide.RightHandDrive,
        "lhd" or "left" or "left-hand drive" or "lefthanddrive" => SteeringSide.LeftHandDrive,
        _ => SteeringSide.Unknown,
    };

    private static FuelType ParseFuel(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "petrol" or "gasoline" or "gas" => FuelType.Petrol,
        "diesel" => FuelType.Diesel,
        "hybrid" => FuelType.Hybrid,
        "plugin_hybrid" or "plug_hybrid" or "plug-in hybrid" or "pluginhybrid" => FuelType.PluginHybrid,
        "electric" or "ev" => FuelType.Electric,
        "lpg" => FuelType.Lpg,
        "cng" => FuelType.Cng,
        "hydrogen" => FuelType.Hydrogen,
        _ => FuelType.Unknown,
    };

    private static Transmission ParseTransmission(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "manual" or "mt" => Transmission.Manual,
        "automatic" or "auto" or "at" => Transmission.Automatic,
        "cvt" or "continuouslyvariable" => Transmission.ContinuouslyVariable,
        "semi_auto" or "semiautomatic" or "semi-automatic" => Transmission.SemiAutomatic,
        "dct" or "dualclutch" or "dual-clutch" => Transmission.DualClutch,
        _ => Transmission.Unknown,
    };

    private static Drivetrain ParseDrivetrain(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "fwd" or "2wd" => Drivetrain.FrontWheelDrive,
        "rwd" => Drivetrain.RearWheelDrive,
        "awd" => Drivetrain.AllWheelDrive,
        "4wd" or "4x4" => Drivetrain.FourWheelDrive,
        _ => Drivetrain.Unknown,
    };

    private static MileageUnit ParseMileageUnit(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "km" or "kilometers" or "kilometres" => MileageUnit.Kilometers,
        "mi" or "miles" => MileageUnit.Miles,
        _ => MileageUnit.Unknown,
    };

    private static PriceType ParsePriceType(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "EXW" or "EXWORKS" => PriceType.ExWorks,
        "FOB" or "FREEONBOARD" => PriceType.FreeOnBoard,
        "CFR" or "CNF" or "C&F" or "COSTANDFREIGHT" => PriceType.CostAndFreight,
        "CIF" or "COSTINSURANCEFREIGHT" => PriceType.CostInsuranceFreight,
        _ => PriceType.Unknown,
    };
}
