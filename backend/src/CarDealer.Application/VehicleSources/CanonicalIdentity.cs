using System.Text.RegularExpressions;
using CarDealer.Domain.Enums;

namespace CarDealer.Application.VehicleSources;

/// <summary>
/// Builds <c>Vehicles.CanonicalHash</c> from the first available strong identifier
/// (docs/spec/04-schema-delta.md section 3.1).
/// </summary>
/// <remarks>
/// Precedence is strict: normalized VIN, else normalized chassis number, else source plus
/// normalized lot number. A null hash never matches anything, including another null, so
/// vehicles without a strong identifier stay distinct and reach the review queue instead of
/// auto-merging (decision D3).
///
/// The reason this is a class of its own with its own tests, rather than three lines inside
/// the normalizer: Carapis returns <c>""</c> - not null - for a missing VIN. Treating an empty
/// string as a value would give every VIN-less vehicle from a source the identical hash, and
/// D3 auto-merges on exact hash equality. All 1,722 SBT Japan vehicles would collapse into
/// one row, visible to every tenant at once under decision D1. Blank is absent. Always.
/// </remarks>
public static class CanonicalIdentity
{
    private static readonly Regex NonAlphanumeric = new("[^A-Z0-9]", RegexOptions.Compiled);

    /// <summary>
    /// Returns the hash and which rule produced it, or <c>(null, null)</c> when the record
    /// carries no strong identifier at all.
    /// </summary>
    public static (string? Hash, CanonicalHashSource? Source) Build(
        string? vin, string? chassisNumber, long vehicleSourceId, string? lotNumber)
    {
        var normalizedVin = Normalize(vin);
        if (normalizedVin is not null)
        {
            return (normalizedVin, CanonicalHashSource.Vin);
        }

        var normalizedChassis = Normalize(chassisNumber);
        if (normalizedChassis is not null)
        {
            return (normalizedChassis, CanonicalHashSource.ChassisNumber);
        }

        var normalizedLot = Normalize(lotNumber);
        if (normalizedLot is not null)
        {
            // Scoped to the source, because lot numbers are only unique within one. This
            // therefore never matches across sources - see docs/spec/07-carapis-api.md
            // section 5.4 for what that costs when no source supplies a VIN.
            return ($"{vehicleSourceId}:{normalizedLot}", CanonicalHashSource.SourceLotNumber);
        }

        return (null, null);
    }

    /// <summary>
    /// Uppercases and strips everything that is not a letter or digit, so that
    /// <c>jtnba1hk9-r3039064</c> and <c>JTNBA1HK9R3039064</c> are one identifier.
    /// </summary>
    /// <returns>Null for null, empty, whitespace, or anything that normalizes to nothing.</returns>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = NonAlphanumeric.Replace(value.ToUpperInvariant(), string.Empty);

        // "-" and "N/A" style placeholders normalize to nothing, or to something too short to
        // be an identifier. Returning them would be worse than returning null: they would
        // match each other.
        return normalized.Length == 0 ? null : normalized;
    }
}
