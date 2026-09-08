# Open Items

Identified during review of [`00-master-prompt.md`](00-master-prompt.md) and
[`01-sql-schema.md`](01-sql-schema.md), and **not** resolved by
[`02-decisions.md`](02-decisions.md).

None of these block Phase 0. Several block Phase 1, and three block any production launch. Each
needs a named owner and a date — an item with neither is not tracked, it is forgotten.

| # | Item | Kind | Blocks | Owner | Due |
| --- | --- | --- | --- | --- | --- |
| [O1](#o1--media-redistribution-rights) | Media redistribution rights | Legal | Phase 1 media | _unassigned_ | _unset_ |
| [O2](#o2--carapis-licensing-gate) | Carapis licensing gate | Legal | Phase 1 | _unassigned_ | _unset_ |
| [O3](#o3--pii-and-data-protection) | PII and data protection | Legal/Eng | Production | _unassigned_ | _unset_ |
| [O4](#o4--pii-redaction-before-ai-calls) | PII redaction before AI calls | Eng | Phase 2 | _unassigned_ | _unset_ |
| [O5](#o5--billing-metering-and-quotas) | Billing, metering and quotas | Product/Eng | Commercial launch | _unassigned_ | _unset_ |
| [O6](#o6--observability-and-alerting) | Observability and alerting | Eng | Production | _unassigned_ | _unset_ |
| [O7](#o7--whatsapp-24-hour-messaging-window) | WhatsApp 24-hour window | Eng | Phase 1 messaging | _unassigned_ | _unset_ |
| [O8](#o8--publicid-coverage) | `PublicId` coverage | Eng | Phase 1 API | _unassigned_ | _unset_ |
| ~~O9~~ | ~~Phase 0 acceptance criteria~~ | Product | — | **Closed** | see [`06-`](06-phase-0-acceptance.md) |
| [O10](#o10--destination-import-eligibility-rules) | Destination import-eligibility rules | Product/Legal | Phase 1 hard filters | _unassigned_ | _unset_ |
| [O11](#o11--saved-search-alerting) | Saved-search alerting | Product | — (scope gap) | _unassigned_ | _unset_ |
| [O12](#o12--environments-backup-and-dr) | Environments, backup and DR | Eng | Production | _unassigned_ | _unset_ |
| [O13](#o13--tenant-settings-and-retention-configuration) | Tenant settings / retention config | Eng | Phase 1 | _unassigned_ | _unset_ |
| [O14](#o14--background-job-library-licensing) | Background job library licensing | Legal | — (minor) | _unassigned_ | _unset_ |

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

Master prompt §8 requires the POC report to *document* unresolved licensing questions. Documenting
is not resolving. As written, an unanswerable legal question passes the gate and Phase 1 gets
built on a provider that may not be usable commercially.

**Change needed:** §8 should require licensing questions **resolved** before Phase 1 starts, with:

- a named owner for the Carapis commercial conversation
- a date by which an answer is required
- a stated fallback path if the answer is no — direct partner feeds (BE FORWARD, SBT, TCV) plus
  dealer CSV/Excel/XML/FTP, all of which are already in master prompt §6's adapter list

The architecture already survives a "no" — that is the entire point of
`IVehicleSourceProvider`. What is missing is the trigger that makes anyone act on it.

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

**Decision needed:** either add `PublicId` to all externally addressable entities, or state
explicitly that internal IDs are acceptable in API routes. The inconsistency is the problem; either
answer is defensible.

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

Flagged as a product scope gap, not a defect. Worth considering for Phase 2 alongside the AI
recommendation work.

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

**Decision needed:** whether to score near-duplicates and surface them for human confirmation.
The `VehicleMatchCandidate` table already exists, is unused, and carries `MatchCandidateStatus`
(`Pending`/`Merged`/`Rejected`) — the schema anticipated exactly this and stopped short of
deciding it.

The shape to evaluate, when it is evaluated:

- score on year + odometer + colour + engine displacement, with the odometer doing most of the
  work, since two different cars rarely share one to the kilometre
- write candidates, never merges; a pair above the threshold becomes a `Pending` row
- a person confirms or rejects, and `CanonicalHashSource` already records which rule matched so
  a VIN match can be trusted more than a scored one during review
- until confirmed, both cars stay in the catalogue and search shows both

**Owner and date required.** Until this is decided, an aggregated catalogue shows duplicates,
and that should be stated to users rather than discovered by them.
