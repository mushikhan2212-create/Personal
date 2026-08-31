# Carapis API — observed contract

Extracted from the vendor's published API documentation (`API Docs.zip`, captured
2026-08-31) — both the rendered page and the generated TypeScript client bundled with it.
The client is the more reliable of the two: it carries the security schemes and URL
templates verbatim, where the rendered page had lost its code samples.

`api.carapis.com` is blocked by this environment's egress policy, so nothing here was
executed from this session. Sections 1-5 are read off the documentation. **Section 5.1 onward
is read off a real response** supplied on 2026-08-31 - a 100-record page from
`GET /apix/catalog_api/vehicles/?available_only=true&body_type=sedan&brand=Toyota&color=white&fuel_type=hybrid`
- and where the two disagree, the response wins and the disagreement is recorded.

The edge cases from that response are kept as a fixture at
`backend/tests/CarDealer.UnitTests/Fixtures/carapis-vehicles-list.json`.

---

## 1. Connection

| | |
| --- | --- |
| Base URL | `https://api.carapis.com` |
| Paths | `/apix/<group>/...` |
| Documented version | `CarAPIS API v1.0.0` |

The base URL carries **no `/v2` segment**, and the version in the document is 1.0.0. A
`https://api.carapis.com/v2` base would place every path one segment too deep.

### Authentication

The generated client declares three accepted schemes per operation:

```
security: [
  { key: "ApiKeyAuth",          name: "X-API-Key", type: "apiKey" },
  { key: "BearerApiKeyAuth",    scheme: "bearer",  type: "http"   },
  { key: "jwtAuthWithLastLogin", scheme: "bearer", type: "http"   },
]
```

So `X-API-Key: <key>` and `Authorization: Bearer <key>` are **both** valid for an API key;
the third is for a browser JWT session and is not our path. The adapter sends `X-API-Key`,
which is the scheme listed first and the one the vendor's own request interceptor sets.

Access tiers, from the endpoint descriptions: a demo key always gets full access; an
authenticated user with an active subscription gets full access; **everything else is
limited to a free tier**. Which fields or row counts the free tier withholds is not
documented, and is a measurement for the POC (master prompt §8, quotas and cost).

### Credential handling

The key is configuration, never source. It belongs in `ConnectionStrings`-style
configuration or a secret store, reached through
`VehicleSourceConfigurations.CredentialReference` (acceptance criterion I1/I3). It must not
appear in `appsettings*.json`, in this repository, or in any log line.

---

## 2. Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/apix/catalog_api/vehicles/` | Paginated vehicle list; 25 query parameters |
| GET | `/apix/catalog_api/vehicles/{id}/` | One vehicle by UUID |
| GET | `/apix/catalog_api/brands/` | Active brands; `limit`, `ordering`, `search` |
| GET | `/apix/catalog_api/models/` | Models; `brand` (slug), `limit`, `ordering`, `search` |
| GET | `/apix/catalog_api/sources/` | Offered listing sources (markets) |
| POST | `/apix/catalog_api/ai-search/` | Natural-language query → structured filters |
| GET | `/apix/catalog_ai_analytics/public/vehicles/{id}/` | Valuation analysis |
| GET | `/apix/catalog_ai_analytics/private/vehicles/{id}/` | Valuation analysis, authenticated |
| GET/POST | `/apix/catalog_export/jobs/...` | Bulk export jobs; documents a `429` |
| POST | `/apix/tax/...` | Import tax and landed-cost calculation |

Phase 0.5 needs `vehicles/`, `vehicles/{id}/`, `brands/`, `models/` and `sources/`. The rest
is recorded so it is not rediscovered later.

## 3. Query conventions

- Ranges use a **`min_` / `max_` prefix** — `min_year`, `max_price` — **not** a `_min` /
  `_max` suffix.
- `brand` and `model` accept a **name, slug or UUID**; the response exposes `brand_slug` and
  `model_slug`. The filter name and the response field name differ.
- An **unrecognised query parameter returns 400** with the list of valid ones. It is not
  ignored. A typo in the adapter fails loudly, which is convenient, but it also means every
  parameter must be spelled exactly.
- `source` filters by source code, slug or UUID, e.g. `encar`, `kbchachacha`, `goonet`.

Filter parameters: `available_only`, `body_type`, `brand`, `color`, `features`, `fuel_type`,
`has_accident`, `inspection_passed`, `is_new_vehicle`, `is_undervalued`, `max_engine_cc`,
`max_mileage`, `max_price`, `max_year`, `min_engine_cc`, `min_mileage`, `min_price`,
`min_year`, `model`, `ordering`, `page`, `page_size`, `search`, `source`, `transmission`.

`ordering` takes a field name, prefixed with `-` for descending.

## 4. Pagination

```json
{ "count": 150, "page": 2, "pages": 15, "page_size": 10,
  "has_next": true, "has_previous": true,
  "next_page": 3, "previous_page": 1, "results": [ ... ] }
```

Page-number based, with the total page count supplied — so a bounded sync can be planned up
front rather than discovered by walking to the end. Master prompt §18 forbids unlimited
synchronization, so `pages` is what the quota is enforced against.

## 5. Vehicle fields

`id` is a **UUID**, not an integer.

| Group | Fields |
| --- | --- |
| Identity | `id`, `listing_id`, `vin`, `vehicle_no`, `listing_url`, `source_code` |
| Classification | `brand_name`, `brand_slug`, `model_name`, `model_slug`, `trim`, `generation`, `year` |
| Price | `price_usd`, `price_original`, `price_original_currency`, `original_msrp` |
| Specification | `mileage`, `engine_cc`, `seat_count`, `fuel_type`, `transmission`, `body_type`, `color`, `drive_type` |
| Condition | `has_accident`, `has_simple_repair`, `has_recall`, `recall_fulfilled`, `warranty_type`, `inspection_passed`, `owner_count`, `is_new_vehicle`, `is_verified` |
| Commercial | `seller_type`, `region`, `source_location`, `description`, `features[]` |
| Valuation | `is_undervalued`, `valuation_score`, `has_valuation`, `has_llm_analysis`, `analysis` |
| Lifecycle | `is_available`, `availability_checked_at`, `first_seen_at`, `last_seen_at`, `status_changed_at` |
| Media | `photos[]` |

### Enumerations

| Field | Values |
| --- | --- |
| `fuel_type` | `gasoline`, `diesel`, `hybrid`, `plug_hybrid`, `electric`, `hydrogen`, `cng`, `lpg`, `other`, `unknown` |
| `transmission` | `manual`, `auto`, `cvt`, `semi_auto`, `dct`, `other`, `unknown` |
| `body_type` | `sedan`, `hatchback`, `coupe`, `convertible`, `suv`, `wagon`, `pickup`, `van`, `minivan`, `crossover`, `truck`, `bus`, `other`, `unknown` |
| `color` | `white`, `black`, `gray`, `silver`, `red`, `blue`, `yellow`, `green`, `brown`, `purple`, `orange`, `pink`, `gold`, `beige`, `other`, `unknown` |
| `drive_type` | `fwd` documented by example; the remaining values are unconfirmed |

Every enumeration carries its own `unknown`, which maps cleanly onto the canonical model's
reserved zero value ([`03-canonical-vehicle-model.md`](03-canonical-vehicle-model.md) §6).

---

## 5.1 What the LIST endpoint actually returns

The documented field list in section 5 is the **detail** response. The list response is a
narrower projection, and the difference decides how the adapter has to work.

Present in the list: `id`, `source_code`, `brand_name`, `brand_slug`, `model_name`,
`model_slug`, `trim`, `year`, `price_usd`, `mileage`, `fuel_type`, `transmission`,
`body_type`, `color`, `seller_type`, `region`, `source_location`, `has_accident`,
`is_new_vehicle`, `is_verified`, `first_seen_at`, `last_seen_at`, `thumb`, `photos`,
`photos_count`, `has_valuation`, `has_llm_analysis`, `analysis`.

**Absent from the list**, though documented on the detail: `listing_id`, `listing_url`, `vin`,
`vehicle_no`, `engine_cc`, `seat_count`, `drive_type`, `price_original`,
`price_original_currency`, `original_msrp`, `description`, `features`, `owner_count`,
`warranty_type`, `inspection_passed`, `has_simple_repair`, `has_recall`, `recall_fulfilled`,
`is_available`, `availability_checked_at`, `status_changed_at`, `generation`.

Two fields appear that the documentation does not mention: `thumb`, a single photo object, and
`photos_count`, the true photo count. **`photos` is truncated to five** regardless -
`photos_count` reached 80 against a five-element array. Full media needs the detail call.

`analysis` is likewise reduced, to `price_status`, `is_undervalued`, `percentile_rank` and
`market_delta_pct`.

### The consequence for deduplication

Decision D3 composes `CanonicalHash` from normalized VIN, else chassis number, else source plus
lot number. **The list endpoint carries none of the three.** No `vin`, no `vehicle_no`, and no
`listing_id` to serve as a lot number.

So a sync built on the list endpoint alone produces `CanonicalHash = NULL` on every row, which
by D3 never matches anything - **nothing auto-merges, and every duplicate goes to the review
queue by hand**. The response demonstrates the cost directly: two `subito` Yaris listings share
trim, year, price and mileage exactly (`1.5h Trend`, 2023, 20500, 52341) and differ only by
region and UUID. Almost certainly one car, and nothing in the payload can prove it.

Either the sync calls the detail endpoint per vehicle to obtain `vin` - at a request cost that
has to be measured against the quota - or the POC accepts a review queue as its steady state.
That is a real decision for the POC report, not a detail.

`id` is stable and unique per vehicle, so it is what `ExternalListingId` should carry. It is a
UUID, not the source's own listing id.

### Media

`photos[].url` and `thumb_url` are **sometimes relative** (`/media/vehicles/...`, Carapis's own
re-hosted WebP) and sometimes absolute. Both forms appear inside a single array, so the adapter
must resolve relative paths against the base URL rather than assume either.

`original_url` always points at the origin marketplace's CDN - `ci.encar.com`,
`picture1.goo-net.com`, `trademe.tmcdn.co.nz`, `img.sbtjapan.com`. That Carapis re-hosts images
under its own `/media/` path, while also exposing the origin URL, is directly relevant to
[O1](05-open-items.md#o1--media-redistribution-rights): storing our own copy would be a third
re-host.

### Data quality observed

The catalog is aggregated from marketplaces of very uneven quality, and the payload shows it.

- **`price_usd` is not reliably USD.** A 2008 Camry on `opensooq_ye` is priced `17022000`; a
  2021 Crown on `goonet` is `313000`, a 2018 Crown on `carsensor` `209000` - those read as
  unconverted local currency. `price_usd` is also nullable. Treating it as a base-currency
  price without a sanity check would corrupt every cross-currency range filter, which is
  precisely what decision D6's index exists to serve. **Do not populate
  `PriceBaseCurrency` from it unguarded**; the outliers must be quarantined, and without
  `price_original_currency` in the list projection there is nothing to reconstruct the true
  price from.
- **`body_type` is unreliable.** The query filtered `body_type=sedan`, and the results include
  a Prius and several Yaris - hatchbacks.
- **Nullable far beyond the documentation.** `year`, `mileage` and `price_usd` all arrive null.
  `has_accident`, `is_verified` and `is_new_vehicle` are **tri-state** - true, false or null -
  so they map to `bool?`, and a null must never be read as false.
- **Internally inconsistent rows.** One record is `is_new_vehicle: true` with `year: 2008` and
  `mileage: 180`.
- `trim` arrives as `""` as well as populated; `source_location` was null on every record.

### Sources, and a correction

The earlier reading of the documentation, which named only `encar`, `kbchachacha` and
`goonet`, understated the breadth. This single page spans 25+ marketplaces across Korea, Japan,
Ireland, New Zealand, Italy, Poland, Portugal, Romania, Canada, the US, the UK, Morocco,
Vietnam, Sri Lanka, Cyprus, Belgium, India, Pakistan and the Gulf.

It also corrects a claim made in section 6 below: **`sbtjapan` is present as a source**, and
so are `goonet` and `carsensor`. SBT is named directly in master prompt section 3, so Carapis
does reach at least one of the intended export channels, not none. The substance of the concern
stands - the records are still domestic-marketplace listings without incoterm, freight or
steering side - but "none of the named sources are available" was wrong.

---

## 6. Mapping onto the canonical model

Straightforward:

| Carapis | Canonical |
| --- | --- |
| `brand_name` / `brand_slug` | `Vehicles.Make` raw, `MakeId` via `SourceMakeModelAliases` |
| `model_name` / `model_slug` | `Vehicles.Model` raw, `ModelId` via alias |
| `trim` | `Variant` |
| `year` | `ModelYear` |
| `engine_cc` | `EngineDisplacementCc` |
| `fuel_type` | `FuelType` — `gasoline`→`Petrol`, `plug_hybrid`→`PluginHybrid` |
| `transmission` | `Transmission` — `auto`→`Automatic`, `cvt`→`ContinuouslyVariable`, `semi_auto`→`SemiAutomatic`, `dct`→`DualClutch` |
| `drive_type` | `Drivetrain` |
| `body_type`, `color` | `BodyType`, `ExteriorColor` |
| `seat_count` | `Seats` |
| `mileage` | `Mileage`, with `MileageUnit = Kilometers` (documented as km) |
| `vin` | `Vin` |
| `id` | `VehicleListings.ExternalListingId` - `listing_id` is not in the list projection |
| `listing_url` | `SourceUrl` - **detail endpoint only** |
| `price_original` + `price_original_currency` | `Price` + `CurrencyCode` - **detail endpoint only** |
| `price_usd` | `PriceBaseCurrency` with `BaseCurrencyCode = USD`, **only after a plausibility check** - see section 5.1 |
| `is_available` | `Status` — `Active` or `Unavailable` |
| `first_seen_at` / `last_seen_at` | `FirstSeenAtUtc` / `LastSeenAtUtc` |
| `photos[]` | `VehicleImages` |
| whole record | `VehicleListings.RawPayload` |

`price_usd` arriving pre-converted is convenient but is **not** a substitute for D6's pinned
rate: the vendor's conversion is unattributed and undated, so a historical report built on it
would still drift. Record it, and keep `ExchangeRateId` null until our own rate is applied.

### What Carapis does not carry

Every field decision [D5](02-decisions.md#d5--full-export-trade-canonical-model) added for the
export trade is **absent from this source**:

| Canonical field | Status in Carapis |
| --- | --- |
| `SteeringSide` | Absent. D5 calls it "arguably the single most-used filter in this trade". |
| `RegistrationDate` | Absent. Only `year`. D5 notes registration date, not model year, is what destination import-age rules key on. |
| `AuctionGrade`, `InteriorGrade`, `InspectionScore` | Absent. Only an `inspection_passed` boolean. |
| `ChassisNumber` | Absent as such. `vehicle_no` may be a registration number; do not assume it is a chassis number without evidence. |
| `PriceType` (EXW/FOB/CFR/CIF) | Absent. Prices read as domestic retail, not an incoterm. |
| `FreightCost`, `PortOfLoading`, `PortOfDischarge` | Absent. |
| `Doors` | Absent. Only `seat_count`. |

This is the completeness measurement master prompt §8 asks for, arriving before the first
call rather than after. It does not invalidate D5 — those columns are nullable by design, and
a dealer CSV or a direct BE FORWARD feed may well populate them — but it does mean **the
Carapis POC cannot exercise them**, and a search screen built only on Carapis data cannot
offer a steering-side filter.

### A larger question this raises

Master prompt §3 frames Phase 0.5 as testing "permitted sources such as BE FORWARD, SBT and
TCV" — the Japanese export trade. Carapis's documented sources are `encar` and `kbchachacha`,
which are **Korean domestic** marketplaces, plus `goonet`, which is Japanese domestic, with
US, EU and CN markets available on demand. Those are domestic retail listings, which is
consistent with the missing incoterm, freight and port fields above.

Whether Carapis is the right provider for a Japan-to-export product is a commercial question,
not a technical one, and it belongs with the licensing gate in
[O2](05-open-items.md#o2--carapis-licensing-gate). The architecture is indifferent — that is
what `IVehicleSourceProvider` is for — but the POC report should answer it explicitly.

Separately, Carapis exposes an **import tax and landed-cost endpoint** (`country_code`,
`importer_type`, `customs_value_usd`, `total_landed_cost_usd`). Total landed cost is listed in
[`03-canonical-vehicle-model.md` §8](03-canonical-vehicle-model.md#8-what-is-deliberately-not-modeled)
as deliberately out of scope because it needs per-country duties. If this endpoint is
trustworthy it removes that objection, and §8 is worth revisiting.

---

## 7. Still unknown

To be answered on first contact, and recorded in the POC report:

- Rate limits and quotas. A `429` is documented on the export endpoints; no limit, window or
  retry header is published. The adapter must handle 429 with backoff regardless.
- What the free tier actually withholds — fields, row counts, or endpoints.
- Whether the detail endpoint's `vin` is populated often enough to make per-vehicle detail
  calls worth their quota cost. This is the question that decides whether deduplication works
  at all - see section 5.1.
- What `price_usd` means on the sources where it is clearly not USD, and whether
  `price_original_currency` on the detail endpoint disambiguates it.
- The full `drive_type` vocabulary. `seller_type` shows `dealer`, `private` and `unknown`.
- The shape of `source_location`, which was null on every record seen.
- Whether `vehicle_no` is a chassis number, a registration number, or something else. This one
  matters: dedup rule 2 keys on chassis number, and a wrong assumption there merges cars that
  are not the same car.
