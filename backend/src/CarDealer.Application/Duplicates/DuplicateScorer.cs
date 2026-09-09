using CarDealer.Application.Formatting;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;

namespace CarDealer.Application.Duplicates;

/// <summary>
/// Decides whether two catalogue vehicles are worth showing a person as a possible duplicate,
/// and how strongly.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <see cref="VehicleSources.CanonicalIdentity"/> cannot do the job on this
/// trade's data. Open item O15 measured the cost on a real corpus: 104 listings from two
/// Japanese exporters, of which twelve pairs are the same twelve cars, and not one carries a
/// VIN or a chassis number. Decision D3 auto-merges only on a strong identifier, so the platform
/// matched none of them - about 12% of an aggregated catalogue duplicated and invisible, which
/// is precisely the comparison a broker's customer came for.
/// </para>
///
/// <para>
/// <b>The odometer is a gate, not a signal, and that is the whole design.</b> The obvious
/// approach - weight several fuzzy signals and threshold the sum - was measured against the same
/// corpus and is wrong here:
/// </para>
///
/// <list type="table">
///   <item><term>year + colour + odometer, exact</term><description>12 pairs, all genuine</description></item>
///   <item><term>odometer within 10 km</term><description>16 pairs; one car draws three rival candidates</description></item>
///   <item><term>odometer within 1%</term><description>28 pairs</description></item>
///   <item><term>year + colour, no odometer</term><description>269 pairs - a queue nobody reviews</description></item>
/// </list>
///
/// <para>
/// The tolerance bands fail on near-new stock, where one exporter had four separate 2026 cars of
/// the same colour reading 4, 9, 11 and 78 km. A band wide enough to absorb a rounding difference
/// is wide enough to make those four indistinguishable, and a review queue that offers three
/// candidates for one car teaches the reviewer to stop trusting it. Exact equality keeps them
/// one-to-one.
/// </para>
///
/// <para>
/// So <see cref="Compare"/> assumes the caller has already blocked on the hard criteria and
/// scores only what corroborates. Nothing here merges anything: the output is a suggestion for a
/// person, per D3 and the remarks on <see cref="VehicleMatchCandidate"/>.
/// </para>
/// </remarks>
public static class DuplicateScorer
{
    /// <summary>
    /// Below this, a pair is not worth a person's attention and is not written at all.
    /// </summary>
    /// <remarks>
    /// The gate has already done the precision work by the time anything is scored, so this is
    /// about spending review attention, not about correctness. It is set so that a pair which
    /// clears the gate but contradicts itself on two independent specs stays out of the queue.
    /// </remarks>
    public const decimal ReviewThreshold = 0.50m;

    /// <summary>
    /// An odometer this low says almost nothing about identity.
    /// </summary>
    /// <remarks>
    /// Two delivery-mileage cars of the same model and year genuinely can read the same number,
    /// and in the O15 corpus several did. A match at 274,570 km is near-certain; a match at 9 km
    /// is a coincidence waiting to happen, so it starts lower and has to earn its score from the
    /// other signals.
    /// </remarks>
    public const int WeakOdometerKm = 1_000;

    /// <summary>
    /// How far two engine displacements may differ and still count as agreeing.
    /// </summary>
    /// <remarks>
    /// Sources round: 1,598 cc is quoted as 1.6 L and comes back as 1,600. That is the same
    /// engine. A genuinely different engine in the same model differs by hundreds.
    /// </remarks>
    public const int EngineToleranceCc = 50;

    /// <summary>
    /// Scores a pair the caller has already blocked, or returns null when they must never be
    /// suggested.
    /// </summary>
    public static DuplicateAssessment? Compare(Vehicle a, Vehicle b)
    {
        // Decisive negative evidence beats every similarity below it. Two vehicles that both
        // carry a VIN and carry different ones are different cars, however identical the rest
        // of the record looks - and a queue that suggests them is a queue that is wrong about
        // the one case it had hard evidence on.
        if (Contradicts(a.Vin, b.Vin) || Contradicts(a.ChassisNumber, b.ChassisNumber))
        {
            return null;
        }

        var signals = new List<DuplicateSignal>();

        // The odometer agreement is the reason this pair exists at all, so it opens the score.
        // What it is worth depends on how much information the number carries.
        var odometer = a.Mileage ?? 0;
        var odometerWeight = odometer >= WeakOdometerKm ? 0.60m : 0.25m;

        signals.Add(new DuplicateSignal(
            "odometer",
            odometerWeight,
            odometer >= WeakOdometerKm
                ? $"both read {odometer:N0} to the kilometre"
                : $"both read {odometer:N0} - delivery mileage says little on its own"));

        // These weights sum with the strong-odometer opener to exactly 1.00, and that is a
        // constraint rather than a coincidence. An earlier set summed to 1.05, which sounds
        // harmless and is not: everything above 1.0 clamps, so a pair agreeing on every signal
        // and a pair silent about its gearbox both scored exactly 1.00, and the queue stopped
        // ordering precisely at the end where the reviewer's attention is worth most.
        Add(signals, "colour", 0.15m, Text(a.ExteriorColor, b.ExteriorColor),
            a.ExteriorColor, b.ExteriorColor);

        Add(signals, "engine", 0.10m, Engine(a.EngineDisplacementCc, b.EngineDisplacementCc),
            Cc(a.EngineDisplacementCc), Cc(b.EngineDisplacementCc));

        // Through SpecWords, because these details are rendered verbatim on the review screen.
        // Interpolated raw, the first version of this said "both RightHandDrive" at a reviewer.
        Add(signals, "transmission", 0.05m, Enum(a.Transmission, b.Transmission),
            SpecWords.Of(a.Transmission), SpecWords.Of(b.Transmission));

        Add(signals, "fuel", 0.04m, Enum(a.FuelType, b.FuelType),
            SpecWords.Of(a.FuelType), SpecWords.Of(b.FuelType));

        Add(signals, "steering", 0.03m, Enum(a.SteeringSide, b.SteeringSide),
            SpecWords.Of(a.SteeringSide), SpecWords.Of(b.SteeringSide));

        Add(signals, "body", 0.03m, Text(a.BodyType, b.BodyType), a.BodyType, b.BodyType);

        // A safety net now rather than a working part of the calculation: contradictions can
        // still drive the sum below zero, and Score is decimal(5,4) with no room for a sign.
        var score = Math.Clamp(signals.Sum(s => s.Weight), 0m, 1m);

        return score < ReviewThreshold ? null : new DuplicateAssessment(score, [.. signals]);
    }

    /// <summary>
    /// Whether two strong identifiers are both present and disagree.
    /// </summary>
    /// <remarks>
    /// Both present is the point. One source supplying a VIN and the other supplying nothing is
    /// the normal case in this trade and is not evidence of anything - treating absence as
    /// disagreement would reject every pair the feature exists to find.
    /// </remarks>
    private static bool Contradicts(string? left, string? right)
    {
        var a = VehicleSources.CanonicalIdentity.Normalize(left);
        var b = VehicleSources.CanonicalIdentity.Normalize(right);

        return a is not null && b is not null && a != b;
    }

    private static void Add(
        List<DuplicateSignal> into, string name, decimal weight,
        Agreement agreement, string? left, string? right)
    {
        switch (agreement)
        {
            case Agreement.Agrees:
                into.Add(new DuplicateSignal(name, weight, $"both {left}"));
                break;

            case Agreement.Differs:
                // Subtracted, not merely withheld. Two cars that agree on model, year and
                // odometer but disagree on the gearbox are either the same car described badly
                // by one source, or not the same car - and the reviewer should meet the second
                // possibility further down the queue than a pair with nothing against it.
                into.Add(new DuplicateSignal(name, -weight, $"{left} vs {right}"));
                break;

            case Agreement.Unknown:
            default:
                // Silence is not disagreement. A field one source omits contributes nothing in
                // either direction, which is why it is not listed as a signal at all.
                break;
        }
    }

    private static Agreement Text(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return Agreement.Unknown;
        }

        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Agreement.Agrees
            : Agreement.Differs;
    }

    private static Agreement Engine(int? left, int? right)
    {
        if (left is not { } a || right is not { } b || a <= 0 || b <= 0)
        {
            return Agreement.Unknown;
        }

        return Math.Abs(a - b) <= EngineToleranceCc ? Agreement.Agrees : Agreement.Differs;
    }

    /// <summary>
    /// Enum agreement, where the enum's <c>Unknown</c> member means the source did not say.
    /// </summary>
    /// <remarks>
    /// Every one of these enums reserves 0 for "not stated", and a normalizer that could not
    /// read a value writes it. Comparing two Unknowns as equal would score a pair for the fact
    /// that neither source mentioned the gearbox.
    /// </remarks>
    private static Agreement Enum<T>(T left, T right) where T : struct, System.Enum
    {
        if (Convert.ToInt32(left) == 0 || Convert.ToInt32(right) == 0)
        {
            return Agreement.Unknown;
        }

        return left.Equals(right) ? Agreement.Agrees : Agreement.Differs;
    }

    private static string? Cc(int? value) => value is { } v and > 0 ? $"{v} cc" : null;

    private enum Agreement
    {
        Unknown,
        Agrees,
        Differs,
    }
}

/// <summary>The verdict on one pair.</summary>
public sealed record DuplicateAssessment(decimal Score, DuplicateSignal[] Signals);

/// <summary>
/// One reason the score is what it is, in words a reviewer reads.
/// </summary>
/// <remarks>
/// Stored on the candidate as JSON because a bare number is not reviewable: "0.85" tells nobody
/// whether to merge, and "both read 56,455 to the kilometre · both Gray · 1598 cc vs 1798 cc"
/// tells them exactly what to check.
/// </remarks>
public sealed record DuplicateSignal(string Name, decimal Weight, string Detail);
