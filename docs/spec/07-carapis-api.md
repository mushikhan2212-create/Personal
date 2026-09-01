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

## 3.1 Pagination is not a rate limit

`page` and `page_size` bound the size of a **response**. They do not bound the **rate** of
requests, and nothing in them prevents a `429`. The two are worth keeping apart because they
solve different problems:

- `page_size` decides how many requests a given coverage costs. Fetching 500 vehicles at
  `page_size=100` is 5 requests; at `page_size=20` it is 25. Raising it is the cheapest way to
  stay inside a quota, and the observed response used `page_size=100`, which is very likely the
  server's cap.
- A **rate limit** is enforced by the server, arrives as `429`, and is unaffected by how large
  the pages are. The documentation shows a `429` on the export endpoints and publishes no
  limit, window, or retry header for the vehicles endpoint.

So the adapter needs both: a large `page_size` and a bounded page count to satisfy master
prompt §18's filters and quotas, **and** exponential backoff on `429` because the ceiling is
undocumented and will be discovered by hitting it.

The dedup path makes this sharper. Section 5.2 establishes that `vin` only arrives on the
detail endpoint, so a page of 100 costs 101 requests, and `page_size` does nothing about the
100 detail calls. Request budget is driven by vehicle count, not page count.

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

## 5.2 What the DETAIL endpoint settles

A detail response for the `encar` Camry above resolves the open questions.

### Deduplication works, through the detail call

`vin` is **populated and real**: `JTNBA1HK9R3039064`, seventeen characters. D3's first rule is
therefore live, and `CanonicalHash` can be built - **but only on the detail path**, since the
list projection omits `vin` entirely.

So the choice named in 5.1 has an answer with a price attached: a sync that wants working
auto-merge must call the detail endpoint once per vehicle. One page of 100 becomes 101
requests. Whether that fits the quota is the measurement the POC still owes.

`listing_id` is the **source's own id** (`42146564`, matching the tail of `listing_url`), which
gives D3's third rule a real lot number and is a better `ExternalListingId` than the Carapis
UUID wherever the detail call is made anyway.

### `vehicle_no` is a registration plate, and must not be treated as a chassis number

`"277가7312"` is a Korean number plate, Hangul syllable and all. Mapping it to
`ChassisNumber` would have fed D3's second rule a value that is neither unique to the car over
time nor a chassis number, and re-plating would silently split one car into two - or worse,
a re-issued plate would merge two cars into one. It stays unmapped.

It is also, in effect, **personal data**: a plate identifies a specific vehicle and through it
an owner. It should not be stored or displayed without a decision under
[O3](05-open-items.md#o3--pii-and-data-protection).

### Price: the detail endpoint carries the truth

`price_original: "30900000.00"` with `price_original_currency: "KRW"`, against
`price_usd: 22000`. 30.9M KRW is roughly 22,000 USD, so the conversion here is sound - which
means the absurd `price_usd` values in the list (17,022,000; 313,000) are **conversion
failures on particular sources**, not a mislabeled field everywhere.

That settles the mapping. Take `price_original` + `price_original_currency` as
`Price`/`CurrencyCode`, and compute `PriceBaseCurrency` ourselves against a pinned
`ExchangeRateId` per decision D6. `price_usd` is a cross-check for the plausibility guard, not
a source of truth: it is unattributed, undated, and demonstrably wrong on some sources.

`original_msrp` is also present, which the canonical model has nowhere to put. Not needed for
the POC; worth noting before someone adds a column for it.

### Fields the detail adds

`generation` (`XV70`), `engine_cc`, `seat_count`, `drive_type`, `owner_count`,
`warranty_type`, `inspection_passed`, `is_available` and `availability_checked_at` - the last
two being what `VehicleStatus` should actually key on, rather than inferring availability from
the listing's presence.

`photo_type` vocabulary is `exterior`, `interior`, `other`. The full 30 photos arrive here
against the list's truncated five.

### Two things not to trust

`features` is `[]` on a car whose own description lists brown leather seats, a smart key, a
rear-view camera and lane departure warning. The options are in free text, not in the array.
Any feature-based filter built on `features` would silently match nothing.

`analysis.actual_price` is `20488` while `price_usd` is `22000`. The valuation block is not
consistent with the listing it describes, so it must not be read as a price. Its
`analysis_updated_at` is fifty seconds after `first_seen_at`, so this is not staleness from a
later price change.

The `description` also arrives with its escaping broken - literal `n` where newlines belong
(`"Sedann### Highlightsn- **Accident-Free**"`) - and this one ends `"yeah yeah yeah"`, which
reads like unreviewed generated content in a production record. Store it raw; do not render it
as Markdown without repairing the escaping, and do not treat it as authoritative.

---

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

## 5.3 The source registry, and what it does to D12

`GET /apix/catalog_api/sources/` returns 84 entries. Three things in it matter.

### All three exporters the master prompt names are present

`beforward`, `sbt_japan` and `tcv` all exist, alongside `sat_japan`, `royal_trading`,
`nikkyo`, `kurumaerabi`, `aucnet` and `yahoo_auctions_jp`. The earlier worry that Carapis might
not reach the Japanese export trade at all was unfounded — it reaches most of it.

### But every Japanese exporter is `on_demand`

Of 14 Japan-region entries, only three are `live`: `carsensor`, `goonet` and
`goonet_exchange`. The first two are **domestic** marketplaces. `goonet_exchange` — Goo-net's
export-facing arm — is the **only live, export-oriented Japanese source**.

The vendor defines `on_demand` as "connected per order". So BE FORWARD, SBT, TCV and SAT Japan
are not simply sitting there to be queried; connecting them looks like a commercial
conversation, which makes [O2](05-open-items.md#o2--carapis-licensing-gate) a blocker for the
POC rather than a Phase 1 formality.

`on_demand` does **not** mean empty, though: `sbtjapan` is flagged `on_demand` and returned
rows in the vehicles response. So the flag is a statement about provisioning, not about whether
data exists today, and the only way to know which of these codes actually yields vehicles is to
ask each one for a count.

### Six sources appear under two or three codes, disagreeing about themselves

| Source | Codes, with region and availability |
| --- | --- |
| Goo-net | `goonet` (japan/**live**), `goo_net` (japan/on_demand), `goo-net` (other/on_demand) |
| Goo-net Exchange | `goonet_exchange` (japan/**live**), `goo_net_exchange` (japan/on_demand) |
| SBT Japan | `sbt_japan` (japan/on_demand), `sbtjapan` (other/on_demand) |
| SAT Japan | `sat_japan` (japan/on_demand), `satjapan` (other/on_demand) |
| KB Chachacha | `kbchachacha` (korea/**live**), `kb_chachacha` (korea/on_demand) |
| eCars Trade | `ecars_trade` (europe/on_demand), `ecarstrade` (other/on_demand) |

The pattern is an underscored code in a real region against an unpunctuated code in region
`other` with a blank country — two naming conventions merged without deduplication. They
disagree on `availability` as well as region, so the flag cannot be trusted per-source without
checking which code carries data.

This is a live hazard for [D12](02-decisions.md#d12--the-poc-syncs-japanese-exporters-only):
filtering on `sbt_japan` and filtering on `sbtjapan` are different queries, and only the second
is known to return anything. **Every source code in the POC configuration must be validated by
a count call before it is trusted**, and configuration must carry the code that works, not the
one that reads more tidily.

### `last_parsed_at` is null on all 84

The documentation says this field "shows crawl freshness". It is null on every source without
exception, so freshness cannot be read from this endpoint at all. Master prompt §8 requires the
POC to measure freshness, so it will have to be derived from `last_seen_at` on the vehicles
themselves.

## 5.4 Japanese stock has no VIN, and the empty string is a trap

The Korean `encar` record in 5.2 carried a real VIN. The SBT Japan record does not:

```json
"vin": "",
"vehicle_no": "",
"generation": "",
"warranty_type": ""
```

**Empty strings, not nulls.** That distinction is the most dangerous thing in this API.

`CanonicalHash` is built from the first available strong identifier. Hash an empty string and
**every VIN-less vehicle from the source gets the same hash**, and D3 auto-merges on exact hash
equality — so a naive implementation would collapse all 1,722 SBT vehicles into one. The
normalizer must treat empty and whitespace-only as **absent**, exactly as it treats null, and
this deserves a test of its own rather than a trusted convention.

### What this does to deduplication

For Japanese export stock, rules 1 and 2 are both unavailable — no VIN, no chassis number.
Rule 3 survives: `listing_id` is `AO4106`, the source's own stock number, which is a real lot
number and appears in `listing_url` too.

But rule 3 keys on **source plus lot number**, so it only ever matches within one source — and
re-ingesting the same listing from the same source is already prevented by the unique index on
`(TenantScope, VehicleSourceId, ExternalListingId)`. So rule 3 adds nothing the schema does not
already do.

The conclusion is worth stating plainly: **`CanonicalHash` cannot merge the same physical car
across two Japanese exporters.** If SBT and TCV both list one car, nothing in the payload
proves it, and D3 sends the pair to the review queue by design. That is D3 working as intended
— it was written to be conservative precisely because a wrong merge is worse than a missed one
— but it means the POC's dedup story for Japanese stock is the review queue, not auto-merge,
and the report should say so rather than implying otherwise.

### Steering side exists, but only in prose

`SteeringSide` was called by [D5](02-decisions.md#d5--full-export-trade-canonical-model) the
single most-used filter in this trade, and section 5.1 recorded it as absent. It is absent as a
**field** — but the SBT description contains `**Steering:** Right-Hand Drive`.

So it is recoverable by parsing free text, for this source, in this description template. That
is worth doing for the POC and worth being honest about: it is a per-source heuristic on
generated prose, not a contract, and it must record its own confidence. A vehicle whose
steering side was inferred from a sentence is not the same fact as one the source declared.

### Price on an exporter is already USD

`price_original: "4290.00"` with `price_original_currency: "USD"` — SBT prices for export in
USD natively, so the KRW conversion problem from 5.2 does not arise here. Note `price_usd`
reads `4300` against an original of `4290`: rounded. Another reason to take `price_original` as
the price and treat `price_usd` as indicative.

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
| `vin` | `Vin` — **empty string means absent**; see §5.4 |
| `listing_id` | `VehicleListings.ExternalListingId` and `Vehicles.LotNumber` (detail only); fall back to `id` on the list path |
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
- **How often `vin` is populated across sources.** It is present and valid on the one `encar`
  record checked, which proves the field is real; it does not prove coverage. A Japanese or
  Italian record may well leave it null, and coverage decides whether the detail call is worth
  its cost. Measure it across sources before committing to a sync shape.
- The quota cost of one detail call per vehicle, which the dedup path now requires.
- The full `drive_type` vocabulary (`fwd` observed). `seller_type` shows `dealer`, `private`
  and `unknown`; `photo_type` shows `exterior`, `interior` and `other`.
- The shape of `source_location`, which was null on every record seen.
- Whether `vehicle_no` is a chassis number, a registration number, or something else. This one
  matters: dedup rule 2 keys on chassis number, and a wrong assumption there merges cars that
  are not the same car.
