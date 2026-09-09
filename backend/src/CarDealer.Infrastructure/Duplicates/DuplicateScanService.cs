using System.Text.Json;
using CarDealer.Application.Abstractions;
using CarDealer.Application.Duplicates;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.Duplicates;

/// <summary>
/// Finds vehicles that are probably the same car and queues them for a person to confirm.
/// </summary>
/// <remarks>
/// <para>
/// Open item O15. The catalogue aggregates several exporters who quote the same wholesale stock
/// at different margins, and none of them supplies a VIN, so <see cref="Vehicle.CanonicalHash"/>
/// is null on most rows and D3's auto-merge never fires. This is the second path: suggest, never
/// merge.
/// </para>
///
/// <para>
/// <b>Duplicates are not only cross-source.</b> The obvious shape is "two exporters, one car",
/// and most are - but the O15 corpus also contains one exporter listing the same car twice under
/// two stock numbers, which the lot-number hash deliberately keeps apart because they really are
/// two listings. Restricting the scan to pairs from different sources would miss it, so the scan
/// does not look at the source at all.
/// </para>
///
/// <para>
/// <b>What it will not pair.</b> Vehicles owned by different tenants, ever - a private-inventory
/// row and a global row are different property, and pairing them is the cross-tenant leak D1
/// exists to prevent. Vehicles already gone. A pair a person has already ruled on, in either
/// direction, which is what stops a rejected suggestion returning every night.
/// </para>
/// </remarks>
public sealed class DuplicateScanService
{
    /// <summary>
    /// How many vehicles may share one blocking key before the group is skipped.
    /// </summary>
    /// <remarks>
    /// A group of n produces n(n-1)/2 pairs, so a pathological key - a feed that reports every
    /// odometer as zero, say - turns one group into tens of thousands of candidates and a review
    /// queue nobody can face. Above this the group is logged and left alone: a scan that quietly
    /// produces a useless queue is worse than one that says it found something it will not guess
    /// about.
    /// </remarks>
    public const int MaxGroupSize = 12;

    private readonly CarDealerDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<DuplicateScanService> _log;

    public DuplicateScanService(
        CarDealerDbContext db, IDateTimeProvider clock, ILogger<DuplicateScanService> log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    public async Task<DuplicateScanResult> ScanAsync(CancellationToken ct = default)
    {
        // IgnoreQueryFilters because the global catalogue is the point: this runs as a
        // background job with no tenant resolved, and the tenant filter would otherwise reduce
        // it to nothing. Ownership is enforced explicitly below instead, by grouping on
        // TenantScope - so a tenant's private car can only ever pair with its own.
        //
        // This translates to a real GROUP BY ... HAVING COUNT(*) > 1 rather than grouping in
        // memory - verified against the generated SQL, because the difference matters: only
        // vehicles that actually collide come back, not the catalogue. There is no covering
        // index on the grouped columns and that is a deliberate omission for now. It would turn
        // one nightly scan's sort into a seek, at the cost of maintaining a six-column index on
        // every row every sync writes, and sync runs far more often than this does.
        var groups = await _db.Vehicles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => v.Mileage != null
                && v.ModelYear != null
                && v.Make != null
                && v.Model != null
                && v.Status != VehicleStatus.Archived
                && v.Status != VehicleStatus.Sold)
            .GroupBy(v => new
            {
                v.TenantScope,
                v.Make,
                v.Model,
                v.ModelYear,
                v.Mileage,
                v.MileageUnit,
            })
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(v => v.Id).ToList())
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (groups.Count == 0)
        {
            return new DuplicateScanResult(0, 0, 0);
        }

        // Everything already ruled on, so a rejected pair is not offered again tomorrow. Read
        // once rather than per pair: the queue is small and the round trips are not.
        var known = await _db.VehicleMatchCandidates
            .AsNoTracking()
            .Select(c => new { c.VehicleId, c.CandidateVehicleId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var seen = known.Select(k => (k.VehicleId, k.CandidateVehicleId)).ToHashSet();

        var ids = groups.SelectMany(g => g).Distinct().ToList();

        var vehicles = await _db.Vehicles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => ids.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var examined = 0;
        var raised = 0;
        var skipped = 0;

        foreach (var group in groups)
        {
            if (group.Count > MaxGroupSize)
            {
                skipped++;

                _log.LogWarning(
                    "Skipped a duplicate group of {Count} vehicles: above the {Max} cap, this "
                    + "would raise {Pairs} candidates on its own. Vehicle ids: {Ids}",
                    group.Count, MaxGroupSize, group.Count * (group.Count - 1) / 2,
                    string.Join(", ", group.Take(20)));

                continue;
            }

            foreach (var (left, right) in Pairs(group))
            {
                examined++;

                if (seen.Contains((left, right)))
                {
                    continue;
                }

                if (!vehicles.TryGetValue(left, out var a) || !vehicles.TryGetValue(right, out var b))
                {
                    continue;
                }

                if (DuplicateScorer.Compare(a, b) is not { } assessment)
                {
                    continue;
                }

                _db.VehicleMatchCandidates.Add(new VehicleMatchCandidate
                {
                    VehicleId = left,
                    CandidateVehicleId = right,
                    Score = assessment.Score,
                    SignalsJson = JsonSerializer.Serialize(assessment.Signals),
                    Status = MatchCandidateStatus.Pending,
                    CreatedAtUtc = now,
                });

                // Guards against the same pair being added twice within one scan, which the
                // unique index would otherwise turn into a failed SaveChanges for the whole run.
                seen.Add((left, right));
                raised++;
            }
        }

        if (raised > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _log.LogInformation(
            "Duplicate scan examined {Examined} pairs across {Groups} groups and raised "
            + "{Raised} candidates ({Skipped} groups skipped as too large).",
            examined, groups.Count, raised, skipped);

        return new DuplicateScanResult(examined, raised, skipped);
    }

    /// <summary>
    /// Every unordered pair in a group, each normalized to (lower id, higher id).
    /// </summary>
    /// <remarks>
    /// The normalization is what makes the unique index on (VehicleId, CandidateVehicleId) mean
    /// anything - without it the same pair is stored twice, once in each order, and the
    /// constraint never fires (docs/spec/04-schema-delta.md section 3.2).
    /// </remarks>
    private static IEnumerable<(long Left, long Right)> Pairs(List<long> ids)
    {
        var ordered = ids.Order().ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                yield return (ordered[i], ordered[j]);
            }
        }
    }
}

/// <summary>What one scan did, for the log and for the tests.</summary>
public sealed record DuplicateScanResult(int PairsExamined, int CandidatesRaised, int GroupsSkipped);
