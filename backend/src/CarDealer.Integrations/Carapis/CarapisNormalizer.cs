using System.Globalization;
using System.Text.Json;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;

namespace CarDealer.Integrations.Carapis;

/// <summary>
/// Turns a stored Carapis payload into canonical entities.
/// </summary>
/// <remarks>
/// Works from the raw payload rather than from a live response, so normalization can be re-run
/// over what was already fetched when the mapping improves - and this mapping will improve,
/// because much of it is provisional (SQL schema spec section 8).
///
/// Every rule here that looks arbitrary is recorded in docs/spec/07-carapis-api.md with the
/// evidence behind it. The short version: blanks are absent, price_usd is not trusted, and
/// nothing is inferred from a field the source did not populate.
/// </remarks>
public sealed class CarapisNormalizer : IVehicleRecordNormalizer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public VehicleSourceProviderType ProviderType => VehicleSourceProviderType.Carapis;

    public NormalizedVehicle? Normalize(RawVehicleRecord record, long vehicleSourceId, string? mediaBaseUrl = null)
    {
        var dto = JsonSerializer.Deserialize<CarapisVehicle>(record.RawPayload, Json);

        if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
        {
            return null;
        }

        var inferred = new List<string>();

        // Identifiers. CanonicalIdentity treats blank as absent, which is the whole ballgame:
        // Carapis returns "" for a missing VIN, and hashing that would merge a source into one
        // vehicle. vehicle_no is deliberately not passed as a chassis number - it is a
        // registration plate.
        var vin = CanonicalIdentity.Normalize(dto.Vin);
        var lot = CanonicalIdentity.Normalize(dto.ListingId);
        var (hash, hashSource) = CanonicalIdentity.Build(vin, chassisNumber: null, vehicleSourceId, lot);

        var steering = InferSteeringSide(dto.Description);
        if (steering != SteeringSide.Unknown)
        {
            inferred.Add(nameof(Vehicle.SteeringSide));
        }

        var vehicle = new Vehicle
        {
            TenantId = null, // Shared source: global catalog (decision D1).
            PublicId = Guid.NewGuid(),
            Make = Blank(dto.BrandName),
            Model = Blank(dto.ModelName),
            Variant = Blank(dto.Trim),
            ModelYear = dto.Year,
            BodyType = Blank(dto.BodyType),
            EngineDisplacementCc = dto.EngineCc,
            Seats = dto.SeatCount,
            FuelType = MapFuel(dto.FuelType),
            Transmission = MapTransmission(dto.Transmission),
            Drivetrain = MapDrivetrain(dto.DriveType),
            SteeringSide = steering,
            ExteriorColor = Blank(dto.Color),
            Mileage = dto.Mileage,

            // The API documents max_mileage in kilometres and the descriptions agree, so the
            // unit is known rather than assumed.
            MileageUnit = dto.Mileage.HasValue ? MileageUnit.Kilometers : MileageUnit.Unknown,

            Vin = vin,
            LotNumber = lot,
            CanonicalHash = hash,
            CanonicalHashSource = hashSource,

            // is_available is tri-state. Null means the source did not say, which is not the
            // same as unavailable, so it maps to Unknown rather than to Unavailable.
            Status = dto.IsAvailable switch
            {
                true => VehicleStatus.Active,
                false => VehicleStatus.Unavailable,
                null => VehicleStatus.Unknown,
            },
        };

        var (price, currency) = ResolvePrice(dto);

        var listing = new VehicleListing
        {
            TenantId = null,
            VehicleSourceId = vehicleSourceId,
            ExternalListingId = dto.ListingId is { Length: > 0 } ? dto.ListingId : dto.Id,
            SourceUrl = Blank(dto.ListingUrl),
            Price = price,
            CurrencyCode = currency,

            // No incoterm is published by this provider. Leaving PriceType Unknown is the
            // honest reading: quoting an FOB price as though it were CIF, or the reverse,
            // misstates the price by the entire cost of shipping.
            PriceType = PriceType.Unknown,

            // PriceBaseCurrency stays null here on purpose. Decision D6 requires a pinned
            // ExchangeRateId, and price_usd carries neither a rate nor a date - and is
            // demonstrably wrong on some sources. The FX step populates it later.
            PriceBaseCurrency = null,
            ExchangeRateId = null,

            RawPayload = record.RawPayload,
            FirstSeenAtUtc = dto.FirstSeenAt ?? record.RetrievedAtUtc,
            LastSeenAtUtc = dto.LastSeenAt ?? record.RetrievedAtUtc,
            LastSyncedAtUtc = record.RetrievedAtUtc,
            IsActive = dto.IsAvailable ?? true,
        };

        var images = dto.Photos
            .Where(p => !string.IsNullOrWhiteSpace(p.Url) || !string.IsNullOrWhiteSpace(p.OriginalUrl))
            .Select(p => new VehicleImage
            {
                TenantId = null,
                ImageUrl = ResolveUrl(p.Url, p.OriginalUrl, mediaBaseUrl),
                SortOrder = p.Position,
                ImageType = Blank(p.PhotoType),
            })
            .ToList();

        return new NormalizedVehicle
        {
            Vehicle = vehicle,
            Listing = listing,
            Images = images,
            InferredFields = inferred,
        };
    }

    /// <summary>
    /// Takes <c>price_original</c> with its stated currency, never <c>price_usd</c>.
    /// </summary>
    /// <remarks>
    /// price_usd is unattributed, undated and wrong on some sources - a 2008 Camry priced at
    /// 17,022,000 "USD". price_original carries its own currency and matched on both records
    /// checked. Where the original is missing there is no price rather than a guessed one.
    /// </remarks>
    private static (decimal? Price, string? Currency) ResolvePrice(CarapisVehicle dto)
    {
        var currency = CanonicalIdentity.Normalize(dto.PriceOriginalCurrency);

        if (currency is { Length: 3 }
            && decimal.TryParse(dto.PriceOriginal, NumberStyles.Any, CultureInfo.InvariantCulture, out var original)
            && original > 0)
        {
            return (original, currency);
        }

        return (null, null);
    }

    /// <summary>
    /// Recovers steering side from the description, for sources whose template states it.
    /// </summary>
    /// <remarks>
    /// D5 calls this the single most-used filter in the export trade, and Carapis has no field
    /// for it. SBT Japan writes "**Steering:** Right-Hand Drive" in prose, so it is recoverable
    /// - but this is a heuristic over generated text, not a contract, which is why the caller
    /// is told it was inferred. A vehicle whose steering side was read out of a sentence is not
    /// the same fact as one the source declared.
    /// </remarks>
    private static SteeringSide InferSteeringSide(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return SteeringSide.Unknown;
        }

        var text = description.ToUpperInvariant();

        var rhd = text.Contains("RIGHT-HAND DRIVE", StringComparison.Ordinal)
            || text.Contains("RIGHT HAND DRIVE", StringComparison.Ordinal);

        var lhd = text.Contains("LEFT-HAND DRIVE", StringComparison.Ordinal)
            || text.Contains("LEFT HAND DRIVE", StringComparison.Ordinal);

        // A description mentioning both says nothing useful; claiming either would be a guess.
        return (rhd, lhd) switch
        {
            (true, false) => SteeringSide.RightHandDrive,
            (false, true) => SteeringSide.LeftHandDrive,
            _ => SteeringSide.Unknown,
        };
    }

    /// <summary>Resolves a possibly-relative media path, falling back to the origin CDN url.</summary>
    private static string ResolveUrl(string? url, string? originalUrl, string? mediaBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return originalUrl ?? string.Empty;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return string.IsNullOrWhiteSpace(mediaBaseUrl)
            ? originalUrl ?? url
            : $"{mediaBaseUrl.TrimEnd('/')}{url}";
    }

    /// <summary>Empty and whitespace are absent, not values. Carapis uses "" freely.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static FuelType MapFuel(string? v) => v switch
    {
        "gasoline" => FuelType.Petrol,
        "diesel" => FuelType.Diesel,
        "hybrid" => FuelType.Hybrid,
        "plug_hybrid" => FuelType.PluginHybrid,
        "electric" => FuelType.Electric,
        "hydrogen" => FuelType.Hydrogen,
        "cng" => FuelType.Cng,
        "lpg" => FuelType.Lpg,
        _ => FuelType.Unknown,
    };

    private static Transmission MapTransmission(string? v) => v switch
    {
        "manual" => Transmission.Manual,
        "auto" => Transmission.Automatic,
        "cvt" => Transmission.ContinuouslyVariable,
        "semi_auto" => Transmission.SemiAutomatic,
        "dct" => Transmission.DualClutch,
        _ => Transmission.Unknown,
    };

    private static Drivetrain MapDrivetrain(string? v) => v switch
    {
        "fwd" => Drivetrain.FrontWheelDrive,
        "rwd" => Drivetrain.RearWheelDrive,
        "awd" => Drivetrain.AllWheelDrive,
        "4wd" => Drivetrain.FourWheelDrive,
        _ => Drivetrain.Unknown,
    };
}
