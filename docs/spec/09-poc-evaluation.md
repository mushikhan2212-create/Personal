# Phase 0.5 POC evaluation

Master prompt [§8](00-master-prompt.md) requires a report recording technical results, cost and
limits, and unresolved licensing questions. This is it.

Every number below was produced by `GET /api/v1/catalog-report` against a live catalogue on
**2026-09-07**, not written from recollection. Re-run that endpoint to regenerate the report
against whatever has been imported since; the prose stays true, the figures move.

## What was measured

| | |
| --- | --- |
| Catalogue | 104 vehicles, 104 active listings, 4,501 images |
| Sources | 2 — BE FORWARD (55 listings), SBT Japan (49) |
| Data | Toyota Corolla Altis export stock, captured 2026-09-07 |

Deliberately one model across two exporters. Overlapping stock is what makes deduplication
testable at all; a hundred unrelated cars would have proved nothing either way.

## Headline: aggregation is worth doing, and the current matching finds none of it

Twelve cars appear in **both** exporters' data. They agree on year, colour and odometer **to the
kilometre**, and the price gap is systematic rather than random:

| Year | Colour | Mileage | BE FORWARD | SBT Japan | Δ |
| --- | --- | --- | --- | --- | --- |
| 2016 | Black | 62,620 km | $5,720 | $6,020 | +5% |
| 2016 | Gray | 54,970 km | $5,720 | $6,020 | +5% |
| 2016 | Gray | 52,540 km | $5,720 | $6,020 | +5% |
| 2016 | Gray | 54,752 km | $5,720 | $6,020 | +5% |
| 2016 | Gray | 58,209 km | $6,050 | $6,350 | +5% |
| 2016 | Gray | 56,135 km | $6,050 | $6,350 | +5% |
| 2016 | Gray | 56,455 km | $6,050 | $6,350 | +5% |
| 2026 | Gray | 78 km | $14,300 | $13,300 | −7% |
| 2026 | Gray | 9 km | $14,300 | $13,300 | −7% |
| 2026 | Gray | 11 km | $14,300 | $13,300 | −7% |
| 2026 | Black | 18 km | $14,300 | $13,300 | −7% |
| 2011 | Black | 72,000 km | $3,200 | *(no price)* | — |

Four consistent +5% pairs and four consistent −7% pairs is two exporters quoting the same stock
at different margins. An odometer agreeing exactly, with the same year and colour, is not
coincidence.

**The platform merged 0 of them.** The report says so plainly:

```
"deduplication": {
  "vehiclesTotal": 104,
  "offeredMoreThanOnce": 0,
  "offeredByMoreThanOneSource": 0,
  "autoMergesRecorded": 0,
  "identifiableByVin": 0,
  "identifiableByChassisOnly": 0,
  "noStrongIdentifier": 104
}
```

This is not a defect. Decision [D3](02-decisions.md) auto-merges only on a strong identifier,
and **neither exporter supplies one**: no VIN, no chassis number, on any of the 104 records. The
architecture behaved exactly as designed, and the design's cost is now measurable — roughly 12%
of this corpus is duplicated and invisible, and a buyer who would have saved $300 does not see
the cheaper offer.

The conclusion is not "loosen D3". It is that matching without a strong identifier needs a
different mechanism, written up as [O15](05-open-items.md).

## Why `chassis_code` cannot be used, with the evidence

Both exporters send a `chassis_code` field. It is tempting, and it is a trap:

| Source | Distinct values | Most repeated |
| --- | --- | --- |
| BE FORWARD | 12 across 50 cars | `"-"` — **27 cars** |
| SBT Japan | 5 across 49 cars | `"COROLLA ALTIS"` — **23 cars** |

Treating that as a chassis number would merge 27 unrelated BE FORWARD cars into a single
vehicle, and 23 SBT cars into another. The catalogue would lose three quarters of its stock to a
field that looks like an identifier.

`VehicleImportRecordConverter` maps it to `ChassisCode` — specification text — and never to
`ChassisNumber`. That guard was written before this data arrived; this data is what proves it
earned its place.

## Identity coverage

```
"identity": { "vinPercent": 0, "chassisPercent": 0,
              "sourceLotOnlyPercent": 100, "noCrossSourceIdentifierPercent": 100 }
```

Identical for both sources. Every record carries the exporter's own stock number
(`stock_id`), which `CanonicalIdentity` hashes **together with the source id** — so it
recognises a car when re-importing the same exporter, and can never match anything in another
exporter's data. Re-importing either file is safe and idempotent; matching across the two is
impossible.

> An earlier version of this report contradicted itself here, showing `noIdentifierPercent: 0`
> per source beside `noStrongIdentifier: 104` overall. Both were true under their own
> definitions, which is worse than one being wrong. The field now states which question it
> answers.

## Field completeness

Percentage of each source's listings carrying the field.

| Field | BE FORWARD | SBT Japan |
| --- | --- | --- |
| make, model, year, mileage | 100% | 100% |
| body, colour, engine cc, fuel, transmission | 100% | 100% |
| steering | 98.2% | 100% |
| variant | 58.2% | 98% |
| listing URL | 100% | 100% |
| photos | 100% | 100% |
| **price** | **100%** | **63.3%** |
| comparable base price | 100% | 63.3% |
| **incoterm** | **0%** | **0%** |
| port of loading | 0% | 0% |

Specification data is excellent — better than the Carapis corpus this phase began with. Three
gaps matter commercially:

- **18 of SBT's 49 listings have no price at all** (`price: null` *and* `currency: null`). They
  import, they appear in search, and they sit outside every price filter and price sort. A third
  of that supplier's stock cannot be shopped by budget.
- **No incoterm anywhere.** All 104 listings price as `Unknown`, so no price here is comparable
  with a quoted FOB or CIF price. The UI shows the tag as `—` rather than implying comparability
  it does not have.
- **No port of loading**, so destination and freight reasoning has nothing to work from.

`variant` at 58.2% for BE FORWARD is inference, not supply: neither exporter sends a variant
field, so `ImportNormalizer.VariantFromTitle` derives it from the listing title and records that
it did. SBT's titles are more consistently structured, hence 98%.

## Freshness

```
beforward : stale 0%, oldest 2026-09-03, newest 2026-09-07
sbtjapan  : stale 0%, oldest 2026-09-07, newest 2026-09-07
```

Nothing is stale, and that is a weaker statement than it looks. Neither file carries a
per-record `lastSeenAtUtc`, so every record inherits the file's `capturedAtUtc` — freshness is
only ever as good as the capture, and the whole file ages together. That is still a real
improvement on the previous provider, which froze `first_seen == last_seen` at capture and never
said so.

BE FORWARD's older bound is a second import: `RealData.json` declares
`sourceCode: "beforward"` and contributed 5 of its 55 listings.

## Search response time

Five realistic searches, timed through the same `ISearchProvider` the screen uses, second run
reported so the figure is steady-state rather than plan compilation.

| Search | Matches | Time |
| --- | --- | --- |
| Everything, first page | 104 | 35 ms |
| Free text: "toyota" | 104 | 32 ms |
| Right-hand drive, 2018 or newer | 25 | 38 ms |
| Diesel under 100,000 km | 0 | 7 ms |
| Cheapest first | 104 | 32 ms |

Comfortably inside any interactive budget at this size. **This does not demonstrate scale** —
104 rows is a rounding error, and the grouping query behind the results is the part that will
degrade first. Decision [D4](02-decisions.md) put search behind an interface precisely so this
measurement, repeated at 100,000 rows, can decide whether a dedicated engine is needed. Until
that measurement exists, it is not needed.

The two 104-match searches returning everything is the corpus, not a bug: it is entirely Toyota
Corollas. "Diesel under 100,000 km" matching nothing is the same fact.

## Images

4,501 images for 104 cars — 19 to 89 per vehicle, median around 44. Photo coverage is 100% for
both sources. Only URLs are stored; nothing is copied, which keeps
[O1](05-open-items.md#o1--media-redistribution-rights) open rather than answered: displaying a
supplier's photographs is not the same as having the right to.

## Quotas and cost

**Not applicable, by decision.** §8 anticipated a metered third-party API. Decision
[D13](02-decisions.md) settled that the platform accepts data and does not fetch it, so there is
no per-request quota, no per-record price and no rate limit to report. The cost of acquiring
data sits with whoever produces the file.

Import cost is bounded and local: 64 MB per upload, the whole document parsed in memory, 104
records in well under a second.

## Unresolved licensing questions

§8 asks that these be documented. They are, and documenting is not resolving —
[O2](05-open-items.md#o2--carapis-licensing-gate) argues that Phase 1 should not begin until
they are answered:

1. **Redistribution.** Data supplied by an exporter is not automatically data a dealer may
   republish to their own customers. Unanswered.
2. **Images** ([O1](05-open-items.md#o1--media-redistribution-rights)). Hot-linking a
   supplier's photographs is the current behaviour and the weakest position of the three.
3. **Provenance.** D13 makes the operator responsible for having the right to supply what they
   import. The platform records where a file came from; it cannot verify the operator was
   entitled to it.

## Verdict

**The ingestion path is proven.** 104 real records from two real exporters normalised into the
canonical model with no failures, high specification completeness, working search, visible
attribution and visible staleness. Phase 0.5's slice — configuration → adapter → normalisation
→ persistence → search API → screen → tests → this report — is complete.

**One capability is measurably missing.** Cross-source matching does not work on this data and
cannot be made to work with the identifiers these exporters supply. That is a Phase 1 decision
([O15](05-open-items.md)), not a Phase 0.5 defect, and the honest position is that a catalogue
aggregating several Japanese exporters will show the same car more than once until it is
resolved.

**Two data-quality gaps should be raised with the suppliers before they are engineered around:**
the missing incoterm across both, and the missing price on a third of SBT's stock. Both are
cheaper to fix at the source than to compensate for downstream.
