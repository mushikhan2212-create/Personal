# Vehicle import format

The JSON contract for loading vehicles into the catalog without an API integration
(decision [D13](02-decisions.md#d13--ingestion-is-source-agnostic-and-the-platform-does-not-scrape)).

A worked file lives at [`examples/import-sample.json`](examples/import-sample.json).

## Why this exists

Carapis is a one-shot crawl. Every record it returns has `first_seen_at` equal to
`last_seen_at`, meaning it saw the listing once and never revisited — so `is_available` is
frozen at the moment of capture and a car sold six weeks ago still reads as for sale. Two of
this format's rules exist directly because of that:

- **`lastSeenAtUtc` is required.** A record that cannot say when it was last confirmed is
  importing the same defect.
- **`priceType` is carried explicitly.** Carapis publishes no incoterm at all, which is why
  every vehicle synced from it prices as `Unknown`. FOB and CIF differ by the entire cost of
  shipping.

## Posting a file

```
POST /api/v1/vehicle-sources/{code}/import          multipart/form-data, field name "file"
POST /api/v1/vehicle-sources/{code}/import?dryRun=true
```

Requires the `vehicles.sync` permission. Maximum 64 MB.

**Run `dryRun=true` first on an unfamiliar file.** It parses, filters and counts exactly as a
real import would — how many records are readable, how many fall inside the source's coverage,
how many already exist — and writes nothing. A malformed file then costs five seconds and a
report rather than a half-finished import to unpick.

The source must already be registered in `VehicleSources`; the import attributes every listing
to it. Whether the cars land in the shared global catalog or one tenant's private inventory is
decided by that row's `TenantId` — null for shared, set for private. Nothing in the file
decides it.

## Document shape

| Field | Required | Meaning |
| --- | --- | --- |
| `sourceCode` | no | Must match the endpoint's `{code}` if present. Omit to accept the endpoint's. Guards against importing one exporter's stock under another's name |
| `capturedAtUtc` | no | When the document was produced. Informational; per-vehicle times govern |
| `vehicles` | **yes** | The records |

## Vehicle record

### Required

| Field | Type | Notes |
| --- | --- | --- |
| `externalId` | string | The producer's own stable id. Re-importing the same id updates rather than duplicates. Falls back to `row-N` if omitted, which makes failures reportable but breaks update-matching |
| `lastSeenAtUtc` | ISO 8601 UTC | When this listing was last confirmed to exist. **A record without it is rejected** — never back-filled with "now", which would assert a confirmation nobody made |

### Identity

| Field | Type | Notes |
| --- | --- | --- |
| `vin` | string | Strongest identifier. Enables auto-merge across sources (decision D3) |
| `chassisNumber` | string | Japanese stock usually has this instead of a VIN |
| `lotNumber` | string | Auction or listing lot |

Blank strings count as absent, never as a value. Supplying `""` for a VIN on every record
would otherwise collapse the entire source into one vehicle — a real failure mode this project
has already hit. With none of the three present the car is still imported; it simply cannot be
deduplicated, and the import response counts it under `withoutStrongIdentifier`.

### Description

| Field | Type | Accepted values |
| --- | --- | --- |
| `make`, `model`, `variant` | string | Free text. Searched as typed |
| `year` | number | Model year |
| `mileage` | number | Paired with `mileageUnit` |
| `mileageUnit` | string | `km`, `mi`. **Absent leaves it Unknown** — not assumed to be km |
| `steering` | string | `rhd`, `lhd` |
| `fuelType` | string | `petrol`, `diesel`, `hybrid`, `plugin_hybrid`, `electric`, `lpg`, `cng`, `hydrogen` |
| `transmission` | string | `manual`, `automatic`, `cvt`, `semi_auto`, `dct` |
| `drivetrain` | string | `fwd`, `rwd`, `awd`, `4wd` |
| `bodyType`, `exteriorColor` | string | Free text |
| `engineCc` | number | Displacement |

Any unrecognised value maps to `Unknown` rather than failing the record. A car with an
unfamiliar fuel type is still a car worth having.

### Commercial

| Field | Type | Notes |
| --- | --- | --- |
| `price` | number | In `currency`, not converted |
| `currency` | string | ISO 4217, e.g. `JPY` |
| `priceType` | string | `EXW`, `FOB`, `CFR`, `CIF`. Absent stays `Unknown` rather than being assumed |
| `locationCountry`, `locationCity`, `portOfLoading` | string | Where the car is |
| `destinationMarkets` | string[] | ISO country codes this listing can ship to. Used by the coverage filter |

`priceBaseCurrency` is deliberately **not** an input. Decision D6 requires a pinned, dated
exchange rate, so the base-currency figure is populated by the FX step from a rate with an id —
never by converting at import time with whatever rate happened to apply.

### Availability and media

| Field | Type | Notes |
| --- | --- | --- |
| `isAvailable` | bool | Tri-state. `true` → Active, `false` → Unavailable, **absent → Unknown** |
| `firstSeenAtUtc` | ISO 8601 UTC | Defaults to `lastSeenAtUtc` |
| `imageUrls` | string[] | Absolute URLs, in display order |
| `listingUrl` | string | The public listing. Shown as attribution, which is a POC acceptance criterion |

`isAvailable` absent means Unknown, and **Unknown vehicles are searchable**. Absence of
evidence that a car is gone is not evidence that it is gone; only `false` hides it.

## Coverage filter

Master prompt §18 forbids unlimited ingestion without filters. A source may carry an
allow-list in `VehicleSources.IngestionFilterJson`:

```json
{
  "makes": ["Toyota", "Nissan", "Honda"],
  "models": ["Hiace", "Land Cruiser", "X-Trail", "Corolla"],
  "destinationMarkets": ["PK", "KE", "TZ"],
  "minYear": 2012,
  "maxRecords": 5000
}
```

Every list is an allow-list, and an empty or absent one means **no restriction on that
dimension** — never "allow nothing". `models` matches as a case-insensitive substring, because
sources spell the same model as `Hiace`, `HIACE VAN` and `Hiace Van 3.0 DX`, and exact
matching would exclude most of the stock the list is meant to include.

Records outside the filter are **counted and reported** as `skippedOutOfScope`, not silently
dropped. A filter that quietly discards half a file is indistinguishable from a file that was
half empty.

## Response

```json
{
  "dryRun": false,
  "recordsInFile": 3,
  "storageReference": "vehicle-imports/beforward-a1b2c3.json",
  "syncJobId": 42,
  "status": "Succeeded",
  "totalRecords": 3,
  "created": 3,
  "updated": 0,
  "failed": 0,
  "autoMerged": 0,
  "withoutStrongIdentifier": 1,
  "skippedOutOfScope": 0,
  "elapsedMs": 88,
  "errorMessage": null
}
```

`storageReference` is where the uploaded bytes were kept, so a failed import can be re-run
against exactly what failed rather than a re-export that may differ. Dry runs store nothing and
report `syncJobId: 0`, because a run that wrote nothing did not happen and should not appear in
the sources screen's "last synced".

## Failure behaviour

| Situation | Result |
| --- | --- |
| File is not valid JSON | 400, nothing imported, parser message names the position |
| `sourceCode` contradicts the endpoint | 400, nothing imported |
| Source code not registered | 404 |
| One record is malformed or missing `lastSeenAtUtc` | That record fails; the run continues and reports it in `SyncJobItems`. One bad row never discards a good file |
| Record outside the coverage filter | Skipped, counted in `skippedOutOfScope` |
| Caller lacks `vehicles.sync` | 403 |
