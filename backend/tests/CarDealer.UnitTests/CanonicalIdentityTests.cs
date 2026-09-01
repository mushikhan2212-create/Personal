using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Enums;

namespace CarDealer.UnitTests;

/// <summary>
/// Guards <c>CanonicalHash</c> composition (docs/spec/04-schema-delta.md section 3.1).
/// </summary>
/// <remarks>
/// The blank-identifier cases are the reason this file exists. Carapis returns "" rather than
/// null for a missing VIN, D3 auto-merges on exact hash equality, and decision D1 makes the
/// catalog global - so a hash built from an empty string would merge an entire source into one
/// vehicle and show it to every tenant at once.
/// </remarks>
public class CanonicalIdentityTests
{
    [Fact]
    public void Vin_wins_over_every_other_identifier()
    {
        var (hash, source) = CanonicalIdentity.Build(
            "JTNBA1HK9R3039064", "CHASSIS123", vehicleSourceId: 7, "AO4106");

        Assert.Equal("JTNBA1HK9R3039064", hash);
        Assert.Equal(CanonicalHashSource.Vin, source);
    }

    [Fact]
    public void Chassis_number_is_used_when_there_is_no_vin()
    {
        var (hash, source) = CanonicalIdentity.Build(null, "NZE-141 0012345", 7, "AO4106");

        Assert.Equal("NZE1410012345", hash);
        Assert.Equal(CanonicalHashSource.ChassisNumber, source);
    }

    [Fact]
    public void Lot_number_is_scoped_to_its_source()
    {
        // Lot numbers are only unique within a source, so the source id is part of the key.
        // Two sources can both call a car "AO4106" and mean different cars.
        var (first, source) = CanonicalIdentity.Build(null, null, vehicleSourceId: 7, "AO4106");
        var (second, _) = CanonicalIdentity.Build(null, null, vehicleSourceId: 9, "AO4106");

        Assert.Equal("7:AO4106", first);
        Assert.Equal("9:AO4106", second);
        Assert.NotEqual(first, second);
        Assert.Equal(CanonicalHashSource.SourceLotNumber, source);
    }

    [Fact]
    public void Vin_normalization_collapses_case_and_punctuation()
    {
        var (upper, _) = CanonicalIdentity.Build("JTNBA1HK9R3039064", null, 7, null);
        var (messy, _) = CanonicalIdentity.Build(" jtnba1hk9-r303 9064 ", null, 7, null);

        Assert.Equal(upper, messy);
    }

    /// <summary>
    /// The case that would collapse a whole source into one vehicle.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("-")]
    [InlineData("---")]
    [InlineData("/")]
    public void A_blank_or_punctuation_only_vin_is_absent_not_a_value(string vin)
    {
        var (hash, source) = CanonicalIdentity.Build(vin, null, 7, null);

        Assert.Null(hash);
        Assert.Null(source);
    }

    [Fact]
    public void Two_records_with_blank_identifiers_do_not_share_a_hash()
    {
        // Both null rather than both equal. A null hash never matches anything, including
        // another null, so these stay distinct rows and reach the review queue instead.
        var (first, _) = CanonicalIdentity.Build("", "", 7, "");
        var (second, _) = CanonicalIdentity.Build("", "", 7, "");

        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public void A_record_with_no_identifiers_at_all_has_no_hash()
    {
        var (hash, source) = CanonicalIdentity.Build(null, null, 7, null);

        Assert.Null(hash);
        Assert.Null(source);
    }
}
