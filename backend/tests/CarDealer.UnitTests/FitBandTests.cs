using CarDealer.Application.AI;

namespace CarDealer.UnitTests;

/// <summary>
/// The broker's rule about limits, which is no longer the model's to keep.
/// </summary>
/// <remarks>
/// Every one of these was previously a sentence in the prompt and a hope. They are here because
/// three revisions of that sentence produced orderings identical to the ones without it: a
/// preference the broker chose has to be something the code holds, not something a provider
/// might honour this month.
/// </remarks>
public sealed class FitBandTests
{
    private static readonly Guid Comfortable = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Scraper = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Oldest = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static RequirementBrief Wants() => new()
    {
        Make = "Toyota",
        MinYear = 2015,
        MaxMileage = 120_000,
        MaxPrice = 8_000m,
    };

    private static CandidateVehicle Car(Guid id, int year, int mileage, decimal price) => new()
    {
        Id = id,
        Make = "Toyota",
        Model = "Corolla",
        Year = year,
        Mileage = mileage,
        MileageUnit = "km",
        RetailPrice = price,
        RetailCurrency = "USD",
    };

    [Fact]
    public void A_car_comfortably_inside_every_limit_is_not_flagged()
    {
        var car = Car(Comfortable, 2017, 65_803, 7_350m);

        Assert.False(FitBands.IsTight(Wants(), car));
        Assert.Null(FitBands.Flag(Wants(), car).CloseToTheirLimits);
    }

    [Fact]
    public void A_car_just_under_the_mileage_ceiling_is_flagged()
    {
        // 118,400 against a ceiling of 120,000. It passed the filter, and passing is exactly
        // what this rule says is not the same as fitting.
        Assert.True(FitBands.IsTight(Wants(), Car(Scraper, 2017, 118_400, 5_600m)));
    }

    [Fact]
    public void The_boundary_is_a_tenth_of_the_ceiling()
    {
        // 108,000 exactly is inside; a kilometre past it is not. Pinned because the whole rule
        // turns on this number and a broker should be able to predict which side a car falls.
        Assert.False(FitBands.IsTight(Wants(), Car(Scraper, 2017, 108_000, 6_000m)));
        Assert.True(FitBands.IsTight(Wants(), Car(Scraper, 2017, 108_001, 6_000m)));
    }

    [Fact]
    public void A_car_at_the_oldest_year_accepted_is_flagged()
    {
        Assert.True(FitBands.IsTight(Wants(), Car(Oldest, 2015, 40_000, 6_000m)));
        Assert.False(FitBands.IsTight(Wants(), Car(Oldest, 2016, 40_000, 6_000m)));
    }

    [Fact]
    public void Being_near_the_budget_is_not_a_tight_fit()
    {
        // Deliberate. Cheapest-first already charges a car for its price, and counting the
        // budget here would charge it twice - which would demote every dear car and collapse
        // the ranking back into the price order it is supposed to improve on.
        Assert.False(FitBands.IsTight(Wants(), Car(Comfortable, 2017, 50_000, 7_990m)));
    }

    [Fact]
    public void A_limit_the_customer_never_set_flags_nothing()
    {
        var open = new RequirementBrief { Make = "Toyota" };

        Assert.False(FitBands.IsTight(open, Car(Scraper, 2015, 250_000, 3_000m)));
    }

    [Fact]
    public void A_car_with_no_mileage_recorded_is_not_flagged()
    {
        // Demoting a car for a figure nobody recorded would be a guess, and this feature is
        // built not to guess. It cannot arise through the matches path anyway - the catalog
        // filter drops an unknown mileage when a ceiling was set.
        var unknown = Car(Comfortable, 2017, 0, 6_000m) with { Mileage = null };

        Assert.False(FitBands.IsTight(Wants(), unknown));
    }

    [Fact]
    public void A_scraper_the_model_ranked_first_ends_up_last()
    {
        // The case that made this code exist. The model put the cheapest car top; it is the one
        // nearly at the customer's mileage ceiling, and the broker said that fits worse.
        var candidates = new List<CandidateVehicle>
        {
            FitBands.Flag(Wants(), Car(Scraper, 2016, 118_400, 5_600m)),
            FitBands.Flag(Wants(), Car(Comfortable, 2017, 65_803, 7_350m)),
        };

        var ranked = FitBands.Apply(candidates,
        [
            new RankedVehicle { Id = Scraper, Rank = 1, Score = 0.9m, Reasons = ["cheapest"] },
            new RankedVehicle { Id = Comfortable, Rank = 2, Score = 0.8m, Reasons = ["low km"] },
        ]);

        Assert.Equal(Comfortable, ranked[0].Id);
        Assert.Equal(1, ranked[0].Rank);
        Assert.Equal(Scraper, ranked[1].Id);
        Assert.Equal(2, ranked[1].Rank);
    }

    [Fact]
    public void Order_within_a_band_is_the_models_own()
    {
        // The half that is still the model's judgement. Two cars on the same side of every
        // limit must come back in the order it chose, however odd that order looks.
        var candidates = new List<CandidateVehicle>
        {
            FitBands.Flag(Wants(), Car(Comfortable, 2017, 65_803, 7_350m)),
            FitBands.Flag(Wants(), Car(Oldest, 2017, 78_382, 7_900m)),
            FitBands.Flag(Wants(), Car(Scraper, 2016, 118_400, 5_600m)),
        };

        var ranked = FitBands.Apply(candidates,
        [
            new RankedVehicle { Id = Scraper, Rank = 1, Score = 0.9m, Reasons = ["a"] },
            new RankedVehicle { Id = Oldest, Rank = 2, Score = 0.8m, Reasons = ["b"] },
            new RankedVehicle { Id = Comfortable, Rank = 3, Score = 0.7m, Reasons = ["c"] },
        ]);

        Assert.Equal([Oldest, Comfortable, Scraper], ranked.Select(r => r.Id));
        Assert.Equal([1, 2, 3], ranked.Select(r => r.Rank));
    }

    [Fact]
    public void When_every_car_scrapes_a_limit_the_ranking_is_untouched()
    {
        // A stock of nothing but high-mileage cars is not a reason to reorder anything. The
        // band is relative, so a uniform one has to be a no-op or the rule would fire on sets
        // where it has nothing to say.
        var candidates = new List<CandidateVehicle>
        {
            FitBands.Flag(Wants(), Car(Scraper, 2017, 118_400, 5_600m)),
            FitBands.Flag(Wants(), Car(Comfortable, 2017, 115_000, 6_100m)),
        };

        Assert.All(candidates, c => Assert.True(c.CloseToTheirLimits));

        var ranked = FitBands.Apply(candidates,
        [
            new RankedVehicle { Id = Scraper, Rank = 1, Score = 0.9m, Reasons = ["a"] },
            new RankedVehicle { Id = Comfortable, Rank = 2, Score = 0.8m, Reasons = ["b"] },
        ]);

        Assert.Equal([Scraper, Comfortable], ranked.Select(r => r.Id));
    }

    [Fact]
    public void Flagging_a_request_leaves_the_requirement_alone()
    {
        var request = new RankingRequest
        {
            Requirement = Wants(),
            Candidates =
            [
                Car(Scraper, 2015, 118_400, 5_600m),
                Car(Comfortable, 2017, 65_803, 7_350m),
            ],
        };

        var flagged = FitBands.Flag(request);

        Assert.Equal(request.Requirement, flagged.Requirement);
        Assert.True(flagged.Candidates[0].CloseToTheirLimits);
        Assert.Null(flagged.Candidates[1].CloseToTheirLimits);
    }
}
