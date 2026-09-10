# Open Items

Identified during review of [`00-master-prompt.md`](00-master-prompt.md) and
[`01-sql-schema.md`](01-sql-schema.md), and **not** resolved by
[`02-decisions.md`](02-decisions.md).

None of these block Phase 0. Several block Phase 1, and three block any production launch. Each
needs a named owner and a date — an item with neither is not tracked, it is forgotten.

| # | Item | Kind | Blocks | Owner | Due |
| --- | --- | --- | --- | --- | --- |
| [O1](#o1--media-redistribution-rights) | Media redistribution rights | Legal | Phase 1 media | _unassigned_ | _unset_ |
| ~~O2~~ | ~~Carapis licensing gate~~ | Legal | — | **Closed** | Not using Carapis, see [O2](#o2--carapis-licensing-gate) |
| [O3](#o3--pii-and-data-protection) | PII and data protection | Legal/Eng | Production | _unassigned_ | _unset_ |
| [O4](#o4--pii-redaction-before-ai-calls) | PII redaction before AI calls | Eng | Phase 2 | _unassigned_ | _unset_ |
| [O5](#o5--billing-metering-and-quotas) | Billing, metering and quotas | Product/Eng | Commercial launch | _unassigned_ | _unset_ |
| [O6](#o6--observability-and-alerting) | Observability and alerting | Eng | Production | _unassigned_ | _unset_ |
| [O7](#o7--whatsapp-24-hour-messaging-window) | WhatsApp 24-hour window | Eng | Phase 1 messaging | _unassigned_ | _unset_ |
| ~~O8~~ | ~~`PublicId` coverage~~ | Eng | — | **Closed** | [D17](02-decisions.md#d17--top-level-route-identifiers-are-guids-nested-ones-may-be-integers) |
| ~~O9~~ | ~~Phase 0 acceptance criteria~~ | Product | — | **Closed** | see [`06-`](06-phase-0-acceptance.md) |
| [O10](#o10--destination-import-eligibility-rules) | Destination import-eligibility rules | Product/Legal | Phase 1 hard filters | _unassigned_ | _unset_ |
| ~~O11~~ | ~~Saved-search alerting~~ | Product | — | **Closed** | Phase 1, see [O11](#o11--saved-search-alerting) |
| [O12](#o12--environments-backup-and-dr) | Environments, backup and DR | Eng | Production | _unassigned_ | _unset_ |
| [O13](#o13--tenant-settings-and-retention-configuration) | Tenant settings / retention config | Eng | Phase 1 | _unassigned_ | _unset_ |
| [O14](#o14--background-job-library-licensing) | Background job library licensing | Legal | — (minor) | _unassigned_ | _unset_ |
| ~~O15~~ | ~~Near-duplicate detection~~ | Product/Eng | — | **Closed** | Phase 1, see [O15](#o15--near-duplicate-detection-without-a-strong-identifier) |

---

## O1 — Media redistribution rights

`VehicleImages.ImageUrl` is a single column that does not distinguish **hot-linking** a
third-party image URL from **copying** the image into our own storage.

The two carry different risks. Hot-linking is fragile (images vanish when the source delists) and
frequently violates provider terms. Copying is robust but asserts a redistribution right that
master prompt §18 explicitly says not to assume for Carapis.

**Decision needed:** per source, may we copy images, hot-link them, or neither?

**Schema consequence:** split into `SourceUrl` and `StorageReference`, so a vehicle can carry both
and the serving strategy becomes a per-source configuration rather than a schema-wide assumption.
Cheap now; a data migration later.

This is a legal question. Do not let it be answered by whoever writes the media pipeline.

## O2 — Carapis licensing gate

**Closed 2026-09-10: the product is not using Carapis.** The owner decided against it, which
answers the licensing question by removing it rather than resolving it. Nothing is owed to
anybody here.

The original concern is kept below because it explains why the fallback existed to fall back
*to*, and that is the part worth remembering.

> Master prompt §8 requires the POC report to *document* unresolved licensing questions.
> Documenting is not resolving. As written, an unanswerable legal question passes the gate and
> Phase 1 gets built on a provider that may not be usable commercially.
>
> **Change needed:** §8 should require licensing questions **resolved** before Phase 1 starts,
> with a named owner for the Carapis commercial conversation, a date by which an answer is
> required, and a stated fallback path if the answer is no — direct partner feeds (BE FORWARD,
> SBT, TCV) plus dealer CSV/Excel/XML/FTP, all of which are already in master prompt §6's
> adapter list.
>
> The architecture already survives a "no" — that is the entire point of
> `IVehicleSourceProvider`. What is missing is the trigger that makes anyone act on it.

That fallback is now the primary path, and it was never hypothetical: the entire Phase 0.5
evidence base — 104 listings across BE FORWARD and SBT Japan, and every duplicate-detection
finding drawn from it ([09-poc-evaluation.md](09-poc-evaluation.md)) — came through the file
import route, not through Carapis. Dropping the provider costs the project no data and no
proven behaviour.

**What is left behind, deliberately.** The `CarDealer.Integrations/Carapis` adapter, its
normalizer and their tests remain in the tree, because `IVehicleSourceProvider` is only
demonstrably an abstraction while something other than the file importer implements it. Delete
Carapis and the interface has one implementation, at which point the next provider — a real
exporter feed — gets built against a shape that was never tested against two. No credential is
configured and nothing calls it at runtime; it is compiled and tested code, not a live
integration.

**One loose end this closure exposes, which is not documentation.** `DatabaseSeeder` registers
two *shared* sources typed `Carapis` — `sbtjapan` and `goonet_exchange` — in every environment.
With Carapis dropped, those rows can now never receive data by any route the product still
uses: a JSON import against them returns 400 by design
([08-import-format.md](08-import-format.md)), and a sync would resolve a normalizer for an API
nobody holds a key to. A fresh database therefore ships two dead sources that a user can see
and select.

That also does not square with the Phase 0.5 evidence, which reports 49 listings under the code
`sbtjapan` loaded through the file importer — the exact combination the guard is supposed to
reject. Either the seeded row was altered by hand in the working database or the guard has a
gap. **Unverified: the live database was not reachable when this was written, and the question
is which of those two it is.** Worth settling before the seeded types are changed, because the
answer decides whether the fix is a one-line seed change or a hole in an import guard.

## O3 — PII and data protection

Master prompt §14 is solid on application security. It is silent on data protection.

The product stores customer names, phone numbers, email addresses and **full WhatsApp message
content**, for a deliberately multi-country customer base. Unaddressed:

- data subject access and erasure requests
- retention periods and automatic deletion
- lawful basis for processing, and where consent is recorded
- data residency — where the database physically lives relative to the customers in it
- processor/controller relationship with tenants (dealers are likely controllers; the platform is
  likely a processor, which changes who answers a subject request)

Schema §11 says retention is "configurable" but nothing stores the configuration — see
[O13](#o13--tenant-settings-and-retention-configuration).

## O4 — PII redaction before AI calls

Master prompt §11 sends conversation text to an AI provider for extraction, summarization and
follow-up suggestion. That text contains customer names, phone numbers and addresses.

Nothing in either document requires redaction before the call, or states what the provider may
retain.

**Decision needed:** redact before sending, rely on a zero-retention provider agreement, or both.
Note that `AIRequests.InputMetadataJson` may itself capture PII — it needs the same treatment as
the payload.

## O5 — Billing, metering and quotas

It is a SaaS with no `Plans`, `Subscriptions` or `UsageCounters`.

Master prompt §18 requires "filters, quotas and caching" on third-party synchronization, but
nothing defines where a quota lives, what it counts, or what happens when a tenant hits one.

`AIRequests.Cost` records spend per request, which is genuinely good — but nothing aggregates it
per tenant, and nothing enforces a ceiling. An unbounded LLM feature set with per-tenant provider
API costs is a real unit-economics risk.

**Decision needed:** metered dimensions (AI tokens, provider API calls, messages, vehicle sync
volume, seats), plan tiers, and enforcement behavior at the limit — hard stop, soft warning, or
overage.

**Settled:** what a plan attaches to. [D14](02-decisions.md#d14--every-account-is-a-tenant-including-a-solo-trader)
fixes every account as a tenant, including the single-person "personal" tier, so a subscription
hangs off `Tenant` and every limit is enforced in the one scope that already exists. The limits
themselves still need somewhere to live — see [O13](#o13--tenant-settings-and-retention-configuration).

## O6 — Observability and alerting

Master prompt §3 specifies structured logging and correlation IDs. There are no metrics, no
distributed tracing and no alerting.

For an integration-heavy product the dominant failure mode is **"a feed went stale and nobody
noticed."** Master prompt §13 lists source freshness as a *report* — a report nobody reads at 2am.

**Needed:** OpenTelemetry metrics and traces, plus alerting on per-source sync freshness, sync
failure rate, webhook processing lag, and AI provider error rate. `VehicleSourceConfigurations`
already stores `LastSuccessAtUtc` and `LastFailureAtUtc`, so the data for a freshness alert exists
— only the alert is missing.

## O7 — WhatsApp 24-hour messaging window

Master prompt §10 requires respecting "messaging windows". WhatsApp's customer service window
opens on an **inbound** message and closes 24 hours later; outside it, only approved templates may
be sent.

`Conversations.LastMessageAtUtc` is direction-agnostic, so it cannot determine whether the window
is open — an outbound message would refresh it and make a closed window look open.

**Schema consequence:** add `Conversations.LastInboundMessageAtUtc`. Pairs with the deferred
`MessageTemplates` and `CustomerOptIns` tables
([`04-schema-delta.md`](04-schema-delta.md#deferred-to-their-phase)).

## O8 — `PublicId` coverage

Schema §2 says public identifiers "may" use `uniqueidentifier`. In practice `PublicId` is present
on `Tenants`, `Users`, `Customers` and `Vehicles`, and absent on `CustomerRequirements`,
`Conversations`, `Messages` and `Tasks`.

If `PublicId` is the external API identifier, those four entities would expose sequential `bigint`
keys in API routes — enumerable, and a rough disclosure of record counts.

**Phase 1 made this worse before fixing it.** Two more integer-keyed routes were added —
`/customers/{publicId}/notes/{noteId}` and `/duplicates/{id}/merge` — which is the argument for
settling it: an unwritten convention decays one endpoint at a time.

**Closed** as decision [D17](02-decisions.md#d17--top-level-route-identifiers-are-guids-nested-ones-may-be-integers).
Neither of the two answers this item offered was taken wholesale. The rule adopted turns on
whether anything unguessable precedes the id:

- **Top level** — `/duplicates/{id}` identifies a record by that id alone, so it can be walked,
  and on a table shared by every tenant a sequential key also discloses the platform-wide volume.
  These carry a `PublicId`. Added to `RequirementAlerts`, `VehicleMatchCandidates`,
  `VehicleMergeHistory` and `Roles`; five routes changed.
- **Nested** — `/customers/{publicId}/notes/3` cannot be reached without the customer's GUID, and
  whoever holds it can already read every note there. `CustomerRequirements` and `CustomerNotes`
  keep their integer keys deliberately, the same shape as
  `/repos/{owner}/{repo}/issues/{number}`.

Enforced by `RouteIdentifierTests` rather than by memory, including an assertion that the rule
is exercised by routes of both shapes so it cannot pass vacuously.

## O9 — Phase 0 acceptance criteria

**Closed.** Resolved by [`06-phase-0-acceptance.md`](06-phase-0-acceptance.md), which gives a §8
equivalent for Phase 0: sections A–K plus a required seed fixture, every item verifiable through
Swagger, a shell command or the test suite.

Raised in priority by [D10](02-decisions.md#d10--phase-0-is-backend-only-swagger-is-the-test-surface):
with no frontend in Phase 0, an explicit checklist is the only way to establish that the
foundation is complete.

Test coverage expectations remain deliberately unquantified — §J requires that specific
high-risk behaviors are tested by name (isolation, token rotation, permission enforcement,
the assignment guard) rather than that a coverage percentage is hit.

## O10 — Destination import-eligibility rules

`CustomerRequirements` carries `DestinationCountryCode`, and
[`03-canonical-vehicle-model.md`](03-canonical-vehicle-model.md) adds `RegistrationDate` to
`Vehicles` — so the data for eligibility filtering will exist. The **rules** do not.

Destination markets apply age limits, emissions standards and steering-side restrictions that vary
by country and change over time. Master prompt §12's "deterministic hard filters" is the natural
home for them.

**Decision needed:** whether to build a maintained rules table, and who maintains it. Encoding
rules wrongly is worse than not encoding them — a wrong rule silently hides valid stock from
customers. Until this is resolved, destination eligibility is **not** a hard filter.

## O11 — Saved-search alerting

Master prompt §15 includes saved searches. §13 includes unmet-demand reporting. Neither phase
includes the feature that connects them: **notify the salesperson when a vehicle matching a saved
customer requirement appears.**

In this trade that is the highest-value CRM feature — stock moves fast and the first dealer to
respond usually wins the sale. It is a small addition on top of the §12 recommendation pipeline,
which already computes exactly this match.

**Closed in Phase 1.** Built as `RequirementAlerts` with an hourly scan over the existing
`ISearchProvider`, an inbox screen and a header bell. Two rules were what the feature turned
out to need, and both are covered by tests that fail without them: an alert is raised only for
a listing first seen *after* the requirement was written (otherwise a new requirement fires an
alert per matching car — 46 of them against the real catalogue), and a unique index on
(requirement, vehicle) makes re-scanning a no-op.

The AI scoring in master prompt §12 remains Phase 2 and stays separate: `VehicleRecommendations`
was deliberately left empty rather than filled with these deterministic matches.

## O12 — Environments, backup and DR

Master prompt §3 specifies "local-first deployment with clean path to cloud" and "CI-ready
structure." Both are directional rather than concrete. Undefined:

- dev / staging / production environments and their promotion path
- zero-downtime migration strategy (schema §12 covers correctness, not availability)
- backup and restore procedure, and whether restore has ever been tested
- RPO and RTO targets

## O13 — Tenant settings and retention configuration

There is no `TenantSettings` table. `Tenants` carries `DefaultCurrencyCode` and
`DefaultCountryCode` and nothing else configurable.

Several requirements assume per-tenant configuration exists: schema §11's "configurable business/
legal retention policy", schema §11's "configurable grace period" before a listing expires, and
[O5](#o5--billing-metering-and-quotas)'s quotas.

**Needed:** a typed `TenantSettings` table or a validated settings document per tenant. Not a
loose key/value bag — retention periods and grace periods are enforced by background jobs, and an
untyped setting will eventually be read as the wrong type by one of them.

## O14 — Background job library licensing

Master prompt §4 names Hangfire "or equivalent". Hangfire's core is free; Hangfire Pro (batches,
job continuations) is commercially licensed.

Minor, and the "or equivalent" hedge covers it — but the choice should be conscious rather than
discovered when a needed feature turns out to be behind the paid tier. The abstraction §4 requires
means the decision stays reversible.

## O15 — Near-duplicate detection without a strong identifier

Measured, not hypothesised. [`09-poc-evaluation.md`](09-poc-evaluation.md) reports 104 real
listings from BE FORWARD and SBT Japan, of which **twelve are the same twelve cars listed by
both**: identical year, colour and odometer to the kilometre, with prices differing by a
consistent +5% or −7% — two exporters quoting the same stock at different margins.

The platform matched **none** of them. Neither exporter supplies a VIN or a chassis number, and
decision [D3](02-decisions.md) auto-merges only on a strong identifier. The architecture is
behaving as specified; the specification's cost is now known — around 12% of an aggregated
Japanese-export catalogue is duplicated and invisible, which is precisely the value a buyer came
for.

**Do not resolve this by loosening D3.** The same POC shows why: BE FORWARD sends the literal
string `"-"` as `chassis_code` for 27 of its 50 cars, and SBT sends `"COROLLA ALTIS"` for 23 of
49. Any rule permissive enough to match the twelve genuine duplicates on weak signals is
permissive enough to merge those into one vehicle each.

**Closed in Phase 1** as decision [D16](02-decisions.md#d16--near-duplicates-are-suggested-never-merged),
built as the review queue this section proposed: `VehicleMatchCandidate` rows written by a
nightly scan, a screen that shows the two cars side by side with the reasons, and a merge that
only happens when a person with `vehicles.merge` says so.

One thing the proposed shape got wrong, and the measurement that corrected it. The suggestion
above was to *score* on year, odometer, colour and engine displacement together. Scoring the
odometer fuzzily is exactly what must not happen — re-measured on the same 104-record corpus:

| Rule | Pairs |
| --- | --- |
| year + colour + odometer, **exact** | **12** — all genuine |
| odometer within 10 km | 16, and one car draws three rival candidates |
| odometer within 1% | 28 |
| year + colour, no odometer | 269 |

The tolerance bands break on near-new stock: SBT alone lists four separate 2026 cars of one
colour reading 4, 9, 11 and 78 km, and any band wide enough to absorb a rounding difference
makes those indistinguishable. So the odometer became a **blocking key** rather than a scored
signal — two vehicles are compared only if make, model, year, odometer and mileage unit all
agree exactly — and the score ranks how much else corroborates, which is what orders the queue.

What the same measurement also found: **duplicates are not only cross-source.** BE FORWARD
lists one car twice under two stock numbers, which the lot-number hash keeps apart because they
genuinely are two listings. The scan therefore ignores the source entirely.

Result on that corpus: 14 pairs examined out of 4,851 possible, 13 raised — the 12 documented
duplicates plus BE FORWARD's own — and one declined, two 2026 cars both reading 6 km in
different colours. What remains open is the threshold's calibration against a larger and more
varied catalogue; the constants carry their reasoning in `DuplicateScorer`.
