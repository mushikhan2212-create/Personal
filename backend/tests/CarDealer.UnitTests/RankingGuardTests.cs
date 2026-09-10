using CarDealer.Application.AI;

namespace CarDealer.UnitTests;

/// <summary>
/// What a model is not allowed to get away with.
/// </summary>
/// <remarks>
/// These are the tests that let the provider be a commercial decision rather than a leap of
/// faith. Each one describes a specific way a plausible-looking ranking can be false, and every
/// one of them is caught without a network call - which is also why the whole suite can run in
/// CI without spending a penny.
/// </remarks>
public sealed class RankingGuardTests
{
    private static readonly Guid CarA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CarB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static RankingRequest Request() => new()
    {
        Requirement = new RequirementBrief { Make = "Toyota", MaxPrice = 7_000m, MinYear = 2016 },
        Candidates =
        [
            new CandidateVehicle
            {
                Id = CarA,
                Make = "Toyota",
                Model = "Corolla",
                Year = 2016,
                Mileage = 62_620,
                MileageUnit = "km",
                EngineCc = 1_800,
                RetailPrice = 6_800m,
                RetailCurrency = "USD",
                OfferCount = 2,
            },
            new CandidateVehicle
            {
                Id = CarB,
                Make = "Toyota",
                Model = "Corolla",
                Year = 2017,
                Mileage = 48_000,
                MileageUnit = "km",
                RetailPrice = 6_950m,
                RetailCurrency = "USD",
            },
        ],
    };

    private static RankedVehicle Entry(Guid id, int rank, params string[] reasons)
        => new() { Id = id, Rank = rank, Score = 0.8m, Reasons = reasons };

    [Fact]
    public void A_sound_ranking_passes()
    {
        // The anti-vacuity case. Without it the rest could all pass by rejecting everything.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarB, 1, "48,000 km, the lower of the two"),
            Entry(CarA, 2, "2016, and offered by 2 sources"),
        ]);

        Assert.Null(rejection);
    }

    [Fact]
    public void A_vehicle_that_was_never_sent_is_refused()
    {
        // The worst failure this feature could have: a car that does not exist, described
        // plausibly, in front of a salesperson about to quote it.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "fine"),
            Entry(CarB, 2, "fine"),
            Entry(Guid.NewGuid(), 3, "a car nobody has"),
        ]);

        Assert.NotNull(rejection);
        Assert.Contains("never sent", rejection);
    }

    [Fact]
    public void Dropping_a_candidate_is_refused()
    {
        // Omitting a car is filtering, and filtering is the deterministic layer's job. A model
        // doing it silently has applied a rule nobody authorised and nobody can see.
        var rejection = RankingGuards.Reject(Request(), [Entry(CarA, 1, "fine")]);

        Assert.NotNull(rejection);
        Assert.Contains("left out", rejection);
    }

    [Fact]
    public void The_same_vehicle_twice_is_refused()
    {
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "fine"),
            Entry(CarA, 2, "fine again"),
        ]);

        Assert.NotNull(rejection);
        Assert.Contains("more than once", rejection);
    }

    [Fact]
    public void Ranks_with_a_gap_are_refused()
    {
        // "Third best of twenty" has to mean something. Ranks 1 and 3 with nothing at 2 are
        // not an ordering, they are two opinions.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "fine"),
            Entry(CarB, 3, "fine"),
        ]);

        Assert.NotNull(rejection);
        Assert.Contains("contiguous", rejection);
    }

    [Fact]
    public void A_score_outside_zero_to_one_is_refused()
    {
        var rejection = RankingGuards.Reject(Request(),
        [
            new RankedVehicle { Id = CarA, Rank = 1, Score = 1.4m, Reasons = ["fine"] },
            Entry(CarB, 2, "fine"),
        ]);

        Assert.NotNull(rejection);
        Assert.Contains("outside the range", rejection);
    }

    [Fact]
    public void A_mileage_the_car_does_not_have_is_refused()
    {
        // The quiet one, and the reason this guard exists at all. The sentence reads perfectly
        // and is false: CarA has 62,620 km on it, not 48,000. Nothing about the output looks
        // wrong - which is exactly why a person would not catch it.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "only 48,000 km"),
            Entry(CarB, 2, "fine"),
        ]);

        Assert.NotNull(rejection);
        Assert.Contains("48,000", rejection);
    }

    [Fact]
    public void A_price_the_car_does_not_have_is_refused()
    {
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "yours for 5,200"),
            Entry(CarB, 2, "fine"),
        ]);

        Assert.NotNull(rejection);
        Assert.Contains("5,200", rejection);
    }

    [Fact]
    public void A_number_the_requirement_stated_is_grounded()
    {
        // "Under the 7,000 ceiling" is a true and useful thing to say, and 7,000 belongs to the
        // requirement rather than to the car. Ground against both or the guard punishes the
        // most helpful sentences the model can write.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "under the 7,000 ceiling"),
            Entry(CarB, 2, "fine"),
        ]);

        Assert.Null(rejection);
    }

    [Fact]
    public void Arithmetic_the_model_did_itself_is_refused()
    {
        // A real and deliberate limitation, recorded here rather than discovered later.
        //
        // "200 under the ceiling" is arithmetic over two grounded figures (7,000 - 6,800) and
        // is perfectly true. The guard rejects it anyway, because there is no way to tell a
        // correct subtraction from an invented number by looking at the output - and a guard
        // that accepts derived figures accepts hallucinated ones with them.
        //
        // The cost is a phrasing the model may not use. Rule 3 of the prompt tells it not to,
        // so this should be rare; if it turns out to be common in practice, the fix is the
        // prompt, not a weaker guard.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "200 under the 7,000 ceiling"),
            Entry(CarB, 2, "fine"),
        ]);

        Assert.NotNull(rejection);
        Assert.Contains("200", rejection);
    }

    [Fact]
    public void Small_numbers_are_left_alone()
    {
        // A reason legitimately says "one of only 2 with a sunroof" or "5% cheaper", and
        // neither 2 nor 5 belongs to any car. Checking them would fire the guard on honest
        // sentences and leave the feature permanently falling back.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "1 of 2 from this source, 5% under the other"),
            Entry(CarB, 2, "fine"),
        ]);

        Assert.Null(rejection);
    }

    [Fact]
    public void Thousands_separators_do_not_matter()
    {
        // The model writes 62,620 for the 62620 the database holds. Those are the same claim,
        // and a guard that could not see that would reject every correct answer.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "62,620 km"),
            Entry(CarB, 2, "48000 km"),
        ]);

        Assert.Null(rejection);
    }

    [Fact]
    public void An_engine_size_the_car_states_is_grounded()
    {
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "1800cc"),
            Entry(CarB, 2, "fine"),
        ]);

        Assert.Null(rejection);
    }

    [Fact]
    public void One_cars_figures_do_not_ground_another_cars_reasons()
    {
        // The subtle version of the mileage test. 48,000 is a real number in this candidate
        // set - it just belongs to the other car. Grounding per-vehicle rather than across the
        // whole set is what catches a swap.
        var rejection = RankingGuards.Reject(Request(),
        [
            Entry(CarA, 1, "48,000 km"),
            Entry(CarB, 2, "fine"),
        ]);

        Assert.NotNull(rejection);
    }
}
