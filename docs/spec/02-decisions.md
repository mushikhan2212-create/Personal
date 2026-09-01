# Architecture Decisions

Decisions taken after review of [`00-master-prompt.md`](00-master-prompt.md) and
[`01-sql-schema.md`](01-sql-schema.md), before any code was written. Each was chosen
deliberately over named alternatives; the rejected options are recorded so a future reader can
tell a decision from an accident.

These extend the two specification documents. They do not override them, and they do not change
the phased build order — master prompt §1 still governs: **Phase 0, then Phase 0.5, then stop and
report.**

Schema consequences are in [`04-schema-delta.md`](04-schema-delta.md).

| # | Decision | Status |
| --- | --- | --- |
| [D1](#d1--global-vehicle-catalog-with-tenant-overlay) | Global vehicle catalog with tenant overlay | Accepted |
| [D2](#d2--multi-tenant-user-membership) | Multi-tenant user membership | Accepted |
| [D3](#d3--deduplication-strong-id-only-auto-merge) | Deduplication: strong-ID-only auto-merge | Accepted |
| [D4](#d4--search-behind-an-abstraction-sql-server-first) | Search behind an abstraction, SQL Server first | Accepted |
| [D5](#d5--full-export-trade-canonical-model) | Full export-trade canonical model | Accepted |
| [D6](#d6--cross-currency-pricing-via-a-normalized-base-price) | Cross-currency pricing via a normalized base price | Accepted |
| [D7](#d7--phase-0-blocker-tables-now-the-rest-at-their-phase) | Phase 0 blocker tables now, the rest at their phase | Accepted |
| [D8](#d8--typescript-on-the-frontend) | TypeScript on the frontend (**amends §4**) | Accepted |
| [D9](#d9--ant-design-as-the-component-library) | Ant Design as the component library | Accepted |
| [D10](#d10--phase-0-is-backend-only-swagger-is-the-test-surface) | Phase 0 is backend-only; Swagger is the test surface | Accepted |
| [D11](#d11--net-10-not-net-8) | .NET 10, not .NET 8 (**amends §4**) | Accepted |
| [D12](#d12--the-poc-syncs-japanese-exporters-only) | The POC syncs Japanese exporters only (**narrows §3**) | Accepted |

---

## D1 — Global vehicle catalog with tenant overlay

### Problem

The schema is internally inconsistent. `Vehicles.TenantId` is non-nullable, implying every
vehicle belongs to exactly one tenant. But `VehicleSources` explicitly supports shared sources
(`TenantId` nullable, plus an `IsShared` flag), implying many tenants read the same upstream
data. Both cannot be true. If twenty tenants subscribe to the same shared Carapis source, either
they each store a private copy of every vehicle, or the vehicle rows are global.

### Decision

**Vehicles from shared sources are global.** `Vehicles.TenantId` becomes nullable: `NULL` means
the global catalog, non-null means a tenant's own private inventory (manual, CSV, Excel, XML,
JSON, FTP). Tenant-specific commercial state lives in a new `TenantVehicles` overlay table.

### Why

- Deduplication and Japanese→English normalization run **once** instead of once per tenant. Both
  are expensive and both improve with more data.
- Storage stays linear in the number of real vehicles, not vehicles × tenants.
- It matches what `VehicleSources.IsShared` already implies.

### Cost accepted

- Tenant isolation for vehicles becomes a **read filter**
  (`TenantId == null || TenantId == current`) rather than a hard row-level equality. That is a
  weaker guard, so schema §9's cross-tenant tests move from good practice to mandatory, and must
  additionally prove a tenant cannot *write* to a global row.
- A bad merge is now visible to every tenant simultaneously. This is the direct reason D3 is
  conservative.
- Nullable `TenantId` breaks the §6 unique constraint, because SQL Server treats NULLs as
  distinct. Fixed with a persisted `TenantScope` computed column — see
  [`04-schema-delta.md`](04-schema-delta.md#1--global-catalog--tenant-overlay).

### Rejected

- **Copy per tenant.** Strongest isolation and the simplest security model, but N× storage and
  dedup runs N times over the same data, producing N chances to merge wrongly.
- **Global canonical, tenant-scoped listings.** Close to the chosen option, but it forces every
  tenant to hold its own listing rows even for shared sources, which reintroduces most of the
  duplication without the isolation benefit.

---

## D2 — Multi-tenant user membership

### Problem

The schema puts `TenantId` on `UserRoles` but not on `Users`, and makes `Users.Email` globally
unique. That shape implies one identity can belong to several tenants, but nothing in either
document states it, and several consequences are unhandled.

### Decision

**Intentional: one global user identity, with membership and roles resolved per tenant.**
Confirmed as a requirement — dealer groups and staff working across branches need it.

### Why

- Matches the schema as written; no migration needed later.
- A salesperson at a dealer group can work across branches with one login.

### Cost accepted

- Login needs a tenant-selection step, and JWTs must carry the active `TenantId`. Tenant
  switching gets its own endpoint and writes an `AuditLogs` row.
- `UserRoles` alone cannot express "invited but no role yet" or "suspended in this tenant only",
  and `Users.Status` is global — using it for per-tenant suspension would lock the user out
  everywhere. Requires a `TenantUsers` membership table.
- `Customers.AssignedUserId` has no constraint tying the assignee to that tenant. A customer
  could be assigned to a user with no membership in the owning tenant. Must be guarded in the
  application layer and covered by an integration test.
- `Roles.Name` is globally unique, which prevents tenant-defined roles. Changed to
  `UNIQUE(TenantId, Name)` with `NULL` reserved for system roles.

### Rejected

- **Tenant-scoped users** (`TenantId` on `Users`, `UNIQUE(TenantId, Email)`). Every FK to `Users`
  becomes automatically tenant-safe, which is a real security benefit — but it blocks dealer
  groups, which is a stated requirement.
- **Global identity limited to one tenant.** Simplest for Phase 0 and relaxable later without
  data migration, but defers a requirement that is already known.

---

## D3 — Deduplication: strong-ID-only auto-merge

### Problem

`Vehicles.CanonicalHash` is the entire deduplication mechanism and is specified in one word. The
same car appears on BE FORWARD, SBT and TCV with different photos, differently rounded mileage
and different prices — and master prompt §7 restricts VIN to "where legally and contractually
available", so the strongest identifier is frequently missing. A hash also only does exact
matching, which cannot collapse those listings.

### Decision

**Auto-merge only on an exact strong identifier** — normalized VIN, else chassis number, else
source lot/stock number. Fuzzy similarity **never** auto-merges; it writes a scored suggestion to
a `VehicleMatchCandidates` review queue for human confirmation. All merges are recorded in
`VehicleMergeHistory` and are reversible.

### Why

- Honors master prompt §3's "conservative deduplication".
- Under D1 the catalog is global, so a wrong merge shows a wrong price or wrong availability to
  every tenant at once. The asymmetry is stark: a missed merge shows a duplicate; a wrong merge
  can lose a sale or sell a car twice.
- The review queue still captures the value of fuzzy matching without betting the catalog on a
  similarity threshold nobody has tuned yet.

### Cost accepted

- Visible duplicates remain in the catalog until a human clears the queue.
- Requires human review capacity, and a UI for it in Phase 1.
- `CanonicalHash` needs an index — it is currently the only unindexed lookup key in the design.

### Rejected

- **Exact hash only, as written.** Zero wrong merges, but leaves duplicates permanently and
  undercuts the "one intelligent workspace" USP.
- **Fuzzy auto-merge above a confidence threshold.** Best consolidation, but no threshold can be
  tuned before real multi-source data exists, and the blast radius under D1 is every tenant.

---

## D4 — Search behind an abstraction, SQL Server first

### Problem

Neither document states a scale target — no vehicle count, tenant count, concurrent users or
latency budget — and master prompt §4 lists no search engine. "Advanced vehicle search" over
5,000 rows and over 3,000,000 rows are different systems, and the §7 index list serves only a
narrow set of query shapes.

### Decision

**Put search behind an `ISearchProvider` abstraction and implement SQL Server first.** Master
prompt §8 already requires the POC to measure response time; that measurement becomes the
decision gate for adding a dedicated search engine.

### Why

- Matches the spec's own adapter philosophy (master prompt §5: all external services behind
  interfaces).
- Buys the option without buying the infrastructure. If p95 fails at realistic volume, the
  adapter is swapped without touching business logic.
- Master prompt §18 forbids unlimited synchronization without filters and quotas, so the catalog
  is bounded by design — SQL Server may well be sufficient.

### Cost accepted

- Index design stays provisional until the POC produces numbers.
- If an engine is needed later, an indexing pipeline and reindex strategy are Phase 1 work.

### Rejected

- **Committing to SQL-Server-only now.** Cheapest, but an unrecoverable guess if volume is high.
- **Adding a search engine on day one.** Removes the risk but adds a cluster, an indexing
  pipeline and a sync-lag failure mode before any evidence they are needed.

---

## D5 — Full export-trade canonical model

### Problem

The canonical model is a generic car, not an export-trade vehicle. It has no steering side, no
auction grade, no registration date and no lot number. `PriceType` exists but is never
enumerated. Meanwhile `CustomerRequirements` already carries `DestinationCountryCode` — so the
demand side knows the destination, but the vehicle side carries nothing to filter eligibility
against, and master prompt §12's "deterministic hard filters" step has nothing to filter on.

### Decision

**Extend the canonical model now**, before normalization is written. Add steering side, auction
grade, inspection score, registration date, lot number, doors and seats to `Vehicles`; add
freight cost, freight currency and port of discharge to `VehicleListings`; enumerate `PriceType`
as `FOB | CIF | CFR`. Add `Makes`, `Models` and `SourceMakeModelAliases` reference tables.

Field-by-field detail is in [`03-canonical-vehicle-model.md`](03-canonical-vehicle-model.md).

### Why

- Steering side (RHD/LHD) is arguably the single most-used filter in this trade and cannot be
  derived from any existing column.
- `RegistrationDate` is distinct from `ModelYear`, and destination import-age rules key on
  registration — a car that cannot legally land is not a match.
- `PriceType` without an enumeration makes cross-source price comparison meaningless; FOB and CIF
  differ by the entire cost of shipping.
- `LotNumber` doubles as a strong identifier for D3, directly improving dedup quality.
- Adding these now avoids re-running normalization against stored raw payloads later.

### Cost accepted

- A wider `Vehicles` table before the POC proves which fields sources actually populate. Fields
  are nullable; the POC's completeness measurement (master prompt §8) will show which ones are
  worth keeping.

### Rejected

- **Minimal set now** (steering side, auction grade, registration date, lot number only).
  Defensible, but freight and price type are needed the moment two sources are compared.
- **Keep the generic model.** Lowest upfront work, but guarantees a reprocessing pass.

---

## D6 — Cross-currency pricing via a normalized base price

### Problem

There is no `ExchangeRates` table. Index `VehicleListings(TenantId, Price, CurrencyCode)` serves
only same-currency filtering, so a buyer searching "under $8,000" against JPY-denominated stock
cannot use an index at all. Master prompt §13 also asks for average price and price trends, which
are not computable across mixed currencies.

### Decision

Add an `ExchangeRates` table and maintain a denormalized `PriceBaseCurrency` on
`VehicleListings`, populated at sync time and indexed for range search. **Pin the rate used** by
storing `ExchangeRateId` on the listing.

### Why

- Makes cross-currency range filters index-servable, which is the common search.
- Pinning the rate stops historical reports from silently rewriting themselves as rates move — a
  price-trend report that changes retroactively is worse than no report.

### Cost accepted

- Denormalized data needs a refresh job when rates change, and a decision on refresh cadence.
- Requires choosing an FX rate source and handling its outages.

### Rejected

- **Convert at query time.** Always uses live rates and adds no columns, but no index can serve
  it, so search degrades sharply with catalog size.
- **Single currency for the POC.** Keeps Phase 0.5 small but guarantees reprocessing of every
  price captured during the POC.

---

## D7 — Phase 0 blocker tables now, the rest at their phase

### Problem

Several master-prompt requirements have no storage anywhere in the schema:

| Requirement | Source | Table |
| --- | --- | --- |
| Token revocation strategy | §14 | missing |
| Users, roles **and permissions** | §3 | only `Roles`/`UserRoles` |
| Webhook idempotency via provider event IDs | §10 | missing |
| Opt-ins, templates, messaging windows | §10 | missing |
| Human approval before external AI messaging | §11 | missing |
| Saved searches | §15 | missing |
| Customer tags, notes timeline, activity timeline | §9 | one `Notes` column |
| Vehicle features/options | §7 | missing |

### Decision

Add **only the Phase 0 blockers now**: `RefreshTokens`, `Permissions`, `RolePermissions`. Defer
the rest to their own phase, added via migrations.

### Why

- §14's token revocation and §3's permissions are explicit **Phase 0** deliverables. Shipping
  Phase 0 without them means shipping it incomplete.
- Everything else belongs to Phase 1 or 2, and schema §10 is explicit: "do not create every
  future table solely for the POC; expand through migrations as Phase 1 begins."

### Cost accepted

- More migrations later, by design.

### Rejected

- **Add every table now.** A complete, stable ERD up front, but directly contradicts schema §10
  and creates tables that stay empty for months.
- **Keep §10's POC minimum exactly.** Would ship Phase 0 with no working token revocation and no
  permissions model, requiring §14 and §3 to be formally deferred.

---

## D8 — TypeScript on the frontend

### Problem

Master prompt §4 specifies "Frontend: JavaScript, React, Vite". For a codebase of this size that
is worth a second look, and converting later is expensive.

The canonical vehicle model alone carries roughly thirty fields and six enumerations
([`03-canonical-vehicle-model.md`](03-canonical-vehicle-model.md)). Add tenant context,
permission checks and the API contracts for every entity, and the number of places where a
frontend/backend mismatch can hide is large.

### Decision

**Use TypeScript.** This is a deliberate amendment to master prompt §4.

### Why

- Master prompt §3 already requires OpenAPI/Swagger. Types can be **generated from that spec**, so
  frontend and backend contracts stay in sync automatically rather than by discipline.
- Enum-heavy domain code is where untyped JavaScript fails quietly: `SteeringSide` and
  `PriceType` are `tinyint` on the wire, and confusing them is a silent bug rather than a crash.
- Cheap now, expensive later. Nothing has been written yet.

### Cost accepted

- Slightly higher barrier for contributors not fluent in TypeScript.
- A build step for type generation, wired into the frontend build.

### Rejected

- **Plain JavaScript as §4 specifies.** Follows the signed-off spec exactly with no amendment,
  but pushes contract mismatches from build time to runtime.

---

## D9 — Ant Design as the component library

### Problem

Master prompt §4 names React and Vite but no component library. The product is data-dense —
inventory grids, faceted search, CRM records, a unified inbox, dashboards — so table and form
quality matters more than visual novelty.

### Decision

**Ant Design.**

### Why

- Built for exactly this class of enterprise CRM/admin product; its tables, filters and forms
  cover the vehicle grid and CRM screens without assembly.
- Strong RTL and i18n support, which matters given §9 stores `PreferredLanguage` and the buyer
  base spans multiple countries and scripts. Retrofitting RTL is painful.
- MIT licensed with no paid tier — unlike MUI, whose X DataGrid puts virtualization, column
  pinning and aggregation behind a commercial licence. The vehicle grid needs those.

### Cost accepted

- A distinctive default look that takes deliberate effort to restyle if strong brand identity is
  wanted later.

### Rejected

- **Mantine.** Excellent DX and more visually neutral, so easier to brand — but less
  batteries-included for complex enterprise tables.
- **MUI.** Largest ecosystem and hiring pool, but the components this product actually needs are
  behind the commercial tier. Same class of licensing trap as the Hangfire note in §4
  ([O14](05-open-items.md#o14--background-job-library-licensing)).
- **Tailwind + headless components.** Maximum control and smallest runtime, but tables, forms,
  modals and date pickers are all assembled by hand before the first screen ships.

---

## D10 — Phase 0 is backend-only; Swagger is the test surface

### Problem

Phase 0 will be tested and signed off before Phase 0.5 begins. Almost everything §3 lists for
Phase 0 is backend, but a few features — login, tenant switching, role administration — are
user-facing, raising the question of whether Phase 0 needs a UI to be verifiable.

### Decision

**No frontend in Phase 0.** The React/Vite application is scaffolded at Phase 0.5, alongside
§17's first vertical slice. Phase 0 is verified through the OpenAPI/Swagger page and the
automated test suite.

D8 and D9 still apply — they are settled now so that the first React code written at Phase 0.5
is written once, not rewritten.

### Why

- OpenAPI/Swagger is already a Phase 0 deliverable in §3, so the test surface exists without
  extra work.
- Keeps Phase 0 tightly scoped to the foundation, which is what the phase is for.
- Master prompt §17 puts the first screen at the Carapis slice. Building UI earlier would
  contradict the spec's own sequencing.

### Cost accepted

- **Verifying multi-tenancy means reading JSON, not clicking through a product.** This raises the
  bar on two things, both now mandatory rather than nice to have:
  1. The seed strategy §3 requires must create **at least two tenants** with users, roles and
     overlapping membership, so cross-tenant isolation can be exercised by hand through Swagger.
  2. The isolation tests in
     [`04-schema-delta.md`](04-schema-delta.md#14-query-filter-and-isolation-tests) are the
     primary evidence of correctness, since no human will see tenant leakage in a UI.
- Acceptance criteria must be explicit, because "it looks right" is not available as a check.
  See [`06-phase-0-acceptance.md`](06-phase-0-acceptance.md).

### Rejected

- **Thin shell** (login, tenant switch, admin screens). Would let multi-tenancy and RBAC be
  verified in a browser, but adds frontend work to a phase whose purpose is the foundation.
- **Thin shell plus dashboard skeleton.** Same, with an earlier read on the overall look.

---

## D11 — .NET 10, not .NET 8

### Problem

Master prompt §4 specifies "ASP.NET Core 8 Web API" and "Entity Framework Core 8 with
migrations". Phase 0 was built on .NET 8 as specified.

**.NET 8 reaches end of support in November 2026** — roughly three months after Phase 0 was
written. Building a platform intended to run for years on a runtime that stops receiving
security patches almost immediately is a poor starting position, and the cost of moving rises
with every phase: at Phase 0 it is a target-framework bump plus package versions, with no
business logic to revalidate.

### Decision

**Target .NET 10 and EF Core 10.** This is a deliberate amendment to master prompt §4, taken
before Phase 0.5 begins.

### Why

- .NET 10 is LTS, supported into November 2028, against .NET 8's November 2026.
- Cheapest possible moment: Phase 0 is foundation only, and the entire test suite exists to
  prove the move did not break anything.
- The EF Core 8 migration required **no changes** — `has-pending-model-changes` reports the
  model and the migration still agree, so there was no schema drift to reconcile.

### Cost accepted

- Requires the .NET 10 SDK locally and in CI. Both are updated.
- Serilog, Swashbuckle and the HealthChecks packages moved a major version alongside it.
  `Asp.Versioning` and Hangfire needed no change.

### Verification

The full suite (41 tests) and all 29 live acceptance checks pass on .NET 10, run twice.
Integration tests got roughly 40% faster. The Production-refuses-without-Redis guard and the
no-secrets-in-logs check were re-verified explicitly.

### Incidental finding

The .NET 10 SDK's NuGet audit surfaced that **Hangfire.SqlServer pulls in Newtonsoft.Json
11.0.1 transitively, which carries a known high-severity advisory**
(GHSA-5crp-9r3c-p9vr). It was present on .NET 8 too and simply went unreported. Both projects
that reference Hangfire now pin Newtonsoft.Json 13.x directly, which wins over the transitive
reference. The pin can be removed once Hangfire's own floor moves past 13.0.1.

### Rejected

- **Stay on .NET 8 as §4 specifies.** Follows the signed-off spec exactly, but knowingly ships
  onto a runtime that leaves support within months.
- **Defer the move to a later phase.** Same end state, strictly more work: every phase adds
  code that the upgrade must then be revalidated against.

---

## D12 — The POC syncs Japanese exporters only

### Problem

Carapis aggregates 25+ marketplaces across Korea, Japan, Ireland, New Zealand, Italy, Poland,
Portugal, Romania, Canada, the US, the UK, Morocco, Vietnam, Sri Lanka, Cyprus, Belgium, India,
Pakistan and the Gulf. Master prompt §3 names BE FORWARD, SBT and TCV — the Japanese export
trade — but does not say what to do with everything else, and §18 forbids unlimited
synchronization without filters and quotas.

Syncing the lot would be both a quota problem and a product problem: a Polish OLX listing and a
Sri Lankan ikman listing are domestic retail ads, priced in local currency to local buyers, and
are not stock a Japanese-export dealer can sell.

### Decision

**The POC syncs Japanese exporters only, four or five of them, selected from what Carapis
actually offers.** Every sync request carries an explicit `source` filter. No unfiltered call
to the vehicles endpoint is made at any point.

### Why

- It is the product's actual market. The canonical model was extended by
  [D5](#d5--full-export-trade-canonical-model) specifically for export-trade fields, and those
  fields only mean anything against export stock.
- It satisfies §18's filter-and-quota requirement with one parameter rather than a
  post-filtering pass over data we paid to fetch.
- It makes the POC's completeness measurement legible. Averaging field coverage across a
  Japanese exporter and a Moroccan classified ad produces a number that describes neither.

### The distinction this rests on

Not every Japanese source is an exporter, and the difference is the whole point:

| Source | Kind | Use to us |
| --- | --- | --- |
| `sbtjapan` | **Exporter** — sells for export, ships worldwide | Directly relevant |
| `goonet` | Japanese **domestic** marketplace | Domestic retail, JPY, sold inside Japan |
| `carsensor` | Japanese **domestic** marketplace | Same |

A domestic Goo-net listing is a car for sale in Japan to a buyer in Japan. Turning it into
export stock means buying it, and that is a different business from reselling an exporter's
listing. Both are "Japanese sources"; only one is a Japanese *exporter*.

### The sources, now confirmed

The `sources/` endpoint has been read. All three exporters the master prompt names exist, and
the earlier worry that Carapis might not reach the export trade was unfounded. The candidate
set for the POC:

Counts were then taken per source, and they decide it:

| Code | Count | Newest record | Verdict |
| --- | --- | --- | --- |
| `sbtjapan` | **1,722** | 2026-07-30 | **In.** Real volume, current |
| `goonet_exchange` | **921** | 2026-07-30 | **In.** Real volume, current |
| `tcv` | 303 | 2026-07-11 | Thin |
| `satjapan` | 70 | 2026-01-28 | Stub, stale by seven months |
| `beforward` | 28 | 2026-01-29 | Stub, stale by seven months |
| `sbt_japan` | **0** | — | Dead code |

**The POC runs on `sbtjapan` and `goonet_exchange`.** Two sources, not five.

BE FORWARD returning 28 vehicles — against the hundreds of thousands on its own site — and
those 28 last seen in January, is what `on_demand` looks like before anyone has connected it:
a frozen sample. TCV's 303 is better but still thin. Building the POC on those would measure
the sample, not the source.

`sbt_japan` returning 0 while `sbtjapan` returns 1,722 confirms the duplicate-code hazard is
real and not theoretical.

### Cost accepted

- **Two sources, not the four or five this decision set out to find.** The intent stands and
  the architecture is unchanged — adding BE FORWARD is one configuration line once it carries
  data — but the POC measures two.
- **[O2](05-open-items.md#o2--carapis-licensing-gate) is now concrete.** Reaching BE FORWARD,
  TCV and SAT Japan at usable volume means a commercial conversation with Carapis about
  connecting `on_demand` sources. That is a question with a price attached, not a legal
  formality, and it belongs in the POC report as such.
- **`availability` does not predict volume.** `sbtjapan` is flagged `on_demand` and has the
  most data of any Japanese source; `goonet_exchange` is `live` and has less. The flag
  describes provisioning, not content, so counts are the only reliable test.
- **Source codes must be validated, not assumed.** Six sources appear under two or three codes
  disagreeing about their own region and availability — `sbtjapan` returns rows where
  `sbt_japan` may not. Every code in configuration is verified by a count call first; see
  [`07-carapis-api.md` §5.3](07-carapis-api.md#53-the-source-registry-and-what-it-does-to-d12).
- If fewer than four exporters can be connected, the POC runs with fewer, or admits the
  domestic `carsensor` and `goonet` as a clearly labelled second tier. It does not pad the
  count with unrelated markets.
- Sources outside Japan stay reachable through the same adapter — they are one `source`
  parameter away — so this narrows the POC, not the architecture.

### Rejected

- **Sync everything Carapis offers.** Widest catalog and no selection to justify, but it
  violates §18, spends quota on stock nobody can sell, and makes every POC measurement an
  average over incomparable markets.
- **Filter after fetching.** Same data volume and same quota cost, with the filtering logic
  duplicated in our code instead of pushed to the provider that already supports it.

---

## Not decided

The following were identified during review and are **not** resolved. They do not block Phase 0.
Each is tracked in [`05-open-items.md`](05-open-items.md) with an owner slot: media
redistribution rights, the Carapis licensing gate, PII/data-protection obligations, PII redaction
before AI calls, billing and quota enforcement, observability and alerting, the WhatsApp
24-hour messaging window, `PublicId` coverage, destination import-eligibility rules,
saved-search alerting, environments/backup/DR, tenant settings and retention configuration, and
background job library licensing.

Phase 0 acceptance criteria were previously open and are now closed by
[`06-phase-0-acceptance.md`](06-phase-0-acceptance.md).
