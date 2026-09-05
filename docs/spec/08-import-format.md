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

Requires the `vehicles.sync` permission, held by **Admin and Tenant Owner only**. Importing
publishes cars into the shared catalogue every tenant reads, so it is an administrative act;
searching what was imported needs only `vehicles.read`, which every role holds. Maximum 64 MB.

**Run `dryRun=true` first on an unfamiliar file.** It parses, filters and counts exactly as a
real import would — how many records are readable, how many fall inside the source's coverage,
how many already exist — and writes nothing. A malformed file then costs five seconds and a
report rather than a half-finished import to unpick.

### The source must exist first, with the right type

`{code}` is a `VehicleSources.Code`. The import attributes every listing to that row, and the
row decides two things the file cannot override:

- **Which adapter reads the payloads.** `ProviderType` must be `DealerJson`. Posting an import
  to a Carapis-typed source (`sbtjapan`, `goonet_exchange`) returns **400** naming the
  mismatch — it used to run and report every record as malformed, blaming the file.
- **Where the cars land.** `TenantId` null puts them in the shared global catalog every tenant
  reads; set, they stay private to that tenant.

A source called **`file-import`** is seeded in every environment, so the commands above work on
a fresh database. To create your own:

```bash
curl -X POST http://localhost:5246/api/v1/vehicle-sources \
  -H "Authorization: Bearer <access token>" \
  -H "Content-Type: application/json" \
  -d '{
        "code": "beforward",
        "name": "BE FORWARD",
        "providerType": "DealerJson",
        "sourceType": "File",
        "baseUrl": "https://www.beforward.jp",
        "isShared": true
      }'
```

`isShared: false` makes it this tenant's private inventory instead. Codes are lower-case
letters, digits, hyphens and underscores, and are unique within their scope — a duplicate
returns 409.

## Field names: both spellings are accepted

The format is published in camelCase, but real producers emit snake_case, and a working
scraper should not have to rename its output to satisfy a naming convention. Every field
answers to both, plus a few names particular sources use:

| Canonical | Also accepted |
| --- | --- |
| `externalId` | `stock_id`, `stockId`, `id`, `listing_id` |
| `lastSeenAtUtc` | `last_seen_at`, `lastSeenAt` |
| `mileageUnit` | `mileage_unit` |
| `fuelType` | `fuel_type` |
| `drivetrain` | `drive_type`, `driveType` |
| `bodyType` | `body_type` |
| `engineCc` | `engine_cc`, `displacement` |
| `exteriorColor` | `exterior_color`, `color` |
| `priceType` | `price_type`, `incoterm` |
| `imageUrls` | `image_urls`, `images`, `photos` |
| `listingUrl` | `listing_url`, `url` |
| `locationCity` | `location_city`, `location` |
| `isAvailable` | `is_available`, `available` |

Numbers may arrive as numbers or as quoted strings. Unknown properties are ignored rather than
rejected — a source adding a field should not break an importer that has not learned it yet,
and the whole payload is preserved verbatim regardless.

## `chassis_code` is not a chassis number

Worth stating on its own, because getting it wrong is silently destructive.

A field named `chassis_code` (also `model_code`) holds a manufacturer's **model** designation —
`M700A`, `5BA-M700A`. Every Toyota Passo of a generation carries the same one, so two different
cars share a value. It is stored as a specification and **never** used for identity; treating it
as a chassis number would merge every car of a model into a single vehicle.

`chassisNumber` / `chassis_number` is the per-car number, and only that is used for matching.

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
| `lastSeenAtUtc` | ISO 8601 UTC | When this listing was last confirmed to exist. Falls back to the document's `capturedAtUtc` when absent — every record in a document was seen when it was made. **Never** back-filled with the import moment, which would assert a confirmation nobody made |

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
| `make`, `model`, `variant` | string | Free text. Searched as typed. When `variant` is absent, a grade is read out of `title` if one is present — `"2022 TOYOTA PASSO 1.0XLPKG"` yields `1.0XLPKG` — and recorded as inferred rather than stated |
| `title` | string | The source's headline. Used only to derive a missing variant |
| `chassisCode` | string | Model code. A specification, never an identifier — see above |
| `seats`, `doors` | number | |
| `conditionNotes` | string | Free text about condition |
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
| One record is malformed | That record fails; the run continues and reports it in `SyncJobItems`. One bad row never discards a good file |
| Record outside the coverage filter | Skipped, counted in `skippedOutOfScope` |
| Caller lacks `vehicles.sync` | 403 |

## Deleting a source

```
DELETE /api/v1/vehicle-sources/{code}?confirm={code}
```

Requires `vehicles.sync`, and the code has to be repeated in `confirm` — this is irreversible,
so the request names what it destroys rather than being one mis-click.

**A car another source still lists is kept.** Only vehicles left with no listing at all are
deleted, along with their photos and any tenant prices set on them. Deleting one exporter never
silently removes stock a different exporter is also selling. The response says how many of each:

```json
{
  "code": "beforward",
  "listingsDeleted": 5,
  "vehiclesDeleted": 5,
  "vehiclesKept": 0,
  "imagesDeleted": 132,
  "syncJobsDeleted": 1,
  "tenantOverlaysDeleted": 0
}
```

In the UI it is the **Delete** button on each source card, which states these consequences
before doing anything.

