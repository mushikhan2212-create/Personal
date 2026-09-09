using CarDealer.Application.Duplicates;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;

namespace CarDealer.UnitTests;

/// <summary>
/// Deciding whether two catalogue rows are worth showing somebody as one car.
/// </summary>
/// <remarks>
/// The cases below are the real ones. Open item O15 measured a corpus of 104 listings from two
/// Japanese exporters containing twelve genuine duplicate pairs and no VINs at all, and the
/// numbers here - 1,598 cc, 56,455 km, the 2026 cars reading 9 and 11 km - are taken from it
/// rather than invented, because the failure this guards against is a scorer that looks
/// reasonable and is tuned to data that does not exist.
/// </remarks>
public sealed class DuplicateScorerTests
{
    private static Vehicle Car(
        int? mileage = 56_455,
        string? colour = "Gray",
        int? cc = 1598,
        Transmission transmission = Transmission.Automatic,
        FuelType fuel = FuelType.Petrol,
        SteeringSide steering = SteeringSide.RightHandDrive,
        string? bodyType = "Sedan",
        string? vin = null,
        string? chassis = null) => new()
        {
            Make = "TOYOTA",
            Model = "Corolla Altis",
            ModelYear = 2016,
            Mileage = mileage,
            MileageUnit = MileageUnit.Kilometers,
            ExteriorColor = colour,
            EngineDisplacementCc = cc,
            Transmission = transmission,
            FuelType = fuel,
            SteeringSide = steering,
            BodyType = bodyType,
            Vin = vin,
            ChassisNumber = chassis,
        };

    [Fact]
    public void The_pairs_the_corpus_actually_contains_are_suggested()
    {
        // Two exporters describing one car: same everything, spelled slightly differently,
        // which is what the raw feeds look like ("Automatic" against "AT" both normalize to
        // the same enum before they reach here).
        var assessment = DuplicateScorer.Compare(Car(), Car(colour: "GRAY"));

        Assert.NotNull(assessment);
        Assert.True(assessment.Score >= DuplicateScorer.ReviewThreshold);
    }

    [Fact]
    public void A_high_odometer_agreeing_is_worth_more_than_a_delivery_reading()
    {
        // The heart of the design. 274,570 km agreeing to the kilometre is near-proof; two
        // brand-new cars both reading 9 km is a coincidence the corpus actually contains - one
        // exporter had four separate 2026 cars at 4, 9, 11 and 78 km.
        var used = DuplicateScorer.Compare(Car(mileage: 274_570), Car(mileage: 274_570));
        var nearlyNew = DuplicateScorer.Compare(Car(mileage: 9), Car(mileage: 9));

        Assert.NotNull(used);
        Assert.NotNull(nearlyNew);
        Assert.True(used.Score > nearlyNew.Score);
    }

    [Fact]
    public void A_delivery_mileage_pair_still_reaches_the_queue_when_everything_else_agrees()
    {
        // It should be ranked below the used cars, not excluded: four of the twelve genuine
        // pairs in the corpus are 2026 stock under 80 km, and dropping them would lose a third
        // of the feature's value.
        var assessment = DuplicateScorer.Compare(Car(mileage: 9), Car(mileage: 9));

        Assert.NotNull(assessment);
        Assert.True(assessment.Score >= DuplicateScorer.ReviewThreshold);
    }

    [Fact]
    public void Two_different_vins_are_never_suggested_however_alike_the_rest_is()
    {
        // Decisive negative evidence. Everything else about these two agrees, and it does not
        // matter: a queue that suggests a pair it had hard evidence against is a queue nobody
        // should trust on the pairs it has no evidence about.
        var assessment = DuplicateScorer.Compare(
            Car(vin: "JTNBA1HK9R3039064"),
            Car(vin: "JTNBA1HK9R3039065"));

        Assert.Null(assessment);
    }

    [Fact]
    public void A_vin_on_only_one_side_is_not_treated_as_disagreement()
    {
        // The normal case in this trade, and the reason the feature exists. Treating "one
        // source supplied a VIN and the other did not" as a contradiction would reject
        // everything.
        var assessment = DuplicateScorer.Compare(Car(vin: "JTNBA1HK9R3039064"), Car());

        Assert.NotNull(assessment);
    }

    [Fact]
    public void Differing_chassis_numbers_are_decisive_too()
    {
        Assert.Null(DuplicateScorer.Compare(
            Car(chassis: "NZE161-3153971"),
            Car(chassis: "NZE161-9999999")));
    }

    [Fact]
    public void A_rounded_engine_size_still_counts_as_the_same_engine()
    {
        // Sources round 1,598 cc to "1.6 L" and back to 1,600. Scoring that as a contradiction
        // would penalise a pair for a unit conversion.
        var rounded = DuplicateScorer.Compare(Car(cc: 1598), Car(cc: 1600));
        var identical = DuplicateScorer.Compare(Car(cc: 1598), Car(cc: 1598));

        Assert.NotNull(rounded);
        Assert.NotNull(identical);
        Assert.Equal(identical.Score, rounded.Score);
    }

    [Fact]
    public void A_genuinely_different_engine_counts_against_the_pair()
    {
        var same = DuplicateScorer.Compare(Car(cc: 1598), Car(cc: 1598));
        var different = DuplicateScorer.Compare(Car(cc: 1598), Car(cc: 1798));

        Assert.NotNull(same);
        Assert.NotNull(different);
        Assert.True(different.Score < same.Score);
    }

    [Fact]
    public void Enough_contradictions_keep_a_pair_out_of_the_queue_entirely()
    {
        // Same model, year and odometer, but a different colour, engine, gearbox, fuel and
        // steering side. That is not one car described twice.
        var assessment = DuplicateScorer.Compare(
            Car(),
            Car(colour: "White", cc: 1798, transmission: Transmission.Manual,
                fuel: FuelType.Diesel, steering: SteeringSide.LeftHandDrive));

        Assert.Null(assessment);
    }

    [Fact]
    public void A_field_neither_source_stated_neither_helps_nor_hurts()
    {
        // Both Unknown must not score as "agrees". Two feeds that are both silent about the
        // gearbox have told us nothing, and rewarding that would let a pair reach the queue on
        // the strength of missing data.
        var silent = DuplicateScorer.Compare(
            Car(transmission: Transmission.Unknown),
            Car(transmission: Transmission.Unknown));

        var stated = DuplicateScorer.Compare(Car(), Car());

        Assert.NotNull(silent);
        Assert.NotNull(stated);
        Assert.True(silent.Score < stated.Score);

        Assert.DoesNotContain(silent.Signals, s => s.Name == "transmission");
    }

    [Fact]
    public void A_missing_colour_is_not_a_colour_mismatch()
    {
        var assessment = DuplicateScorer.Compare(Car(colour: null), Car(colour: "Gray"));

        Assert.NotNull(assessment);
        Assert.DoesNotContain(assessment.Signals, s => s.Name == "colour");
    }

    [Fact]
    public void Every_suggestion_carries_the_reasons_for_it()
    {
        // A bare score is not reviewable. The reviewer needs to see what agreed before they
        // archive one of two cars for every tenant at once.
        var assessment = DuplicateScorer.Compare(Car(), Car());

        Assert.NotNull(assessment);
        Assert.NotEmpty(assessment.Signals);
        Assert.Contains(assessment.Signals, s => s.Name == "odometer");
        Assert.All(assessment.Signals, s => Assert.False(string.IsNullOrWhiteSpace(s.Detail)));
    }

    [Fact]
    public void A_score_never_exceeds_one()
    {
        // Score is decimal(5,4) in the database, so a sum past 1.0 is a write failure rather
        // than a rounding curiosity.
        var assessment = DuplicateScorer.Compare(Car(mileage: 274_570), Car(mileage: 274_570));

        Assert.NotNull(assessment);
        Assert.InRange(assessment.Score, 0m, 1m);
    }

    [Fact]
    public void A_pair_agreeing_on_everything_scores_exactly_one()
    {
        // Not a vanity assertion. The weights are meant to total 1.00, and if they ever total
        // more the clamp swallows the excess silently: two pairs of different quality both come
        // back as 1.00 and the queue stops ranking them. That happened once, and the only
        // symptom was a test about missing gearboxes failing for a reason that looked unrelated.
        var assessment = DuplicateScorer.Compare(Car(mileage: 274_570), Car(mileage: 274_570));

        Assert.NotNull(assessment);
        Assert.Equal(1.00m, assessment.Score);
    }
}
