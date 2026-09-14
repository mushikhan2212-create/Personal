# Phase 1 — Acceptance Criteria

The equivalent of [`06-phase-0-acceptance.md`](06-phase-0-acceptance.md) for Phase 1. Phase 0 was
signed off against a written checklist; Phase 1 was built without one, and this repairs that
after the fact rather than before it — which is itself worth recording, because writing the bar
afterwards is easier to pass than writing it first.

Master prompt §3 lists eight bullets for Phase 1. Several are **not** built, some deliberately
and some because they are blocked on approvals nobody has yet applied for. This document says
which is which, because "Phase 1 is done" and "everything in §3 exists" are not the same claim
and only the first is being made.

**Phase 1 is accepted when A–F pass and every §3 bullet is either passing or recorded in
[§X](#x-what-is-not-built) with a reason.** Phase 2 does not begin before then, and
[§G](#g-what-gates-phase-2) lists what must be answered even after this document is signed.

---

## A. Vehicle aggregation and search

Master prompt §3: *multi-source vehicle aggregation through provider adapters*, *advanced
vehicle search*, *vehicle detail/media/source attribution*.

| # | Criterion | How to verify |
| --- | --- | --- |
| A1 | Vehicles from two or more sources appear in one catalogue | Import two files, search |
| A2 | Every listing keeps its source attribution on screen | Vehicle card and detail page |
| A3 | Search filters on make, model, body, steering, fuel, transmission, year, mileage and price | Filters drawer; `SearchFilterEndpointTests` |
| A4 | Every filter a saved requirement matches on can be typed by hand and give the same cars | Compare a requirement's matches against the same filters entered manually |
| A5 | Data age is visible on every result, and stale records are marked | Card footer; `STALE_AFTER_DAYS` |
| A6 | Adding a source needs `vehicles.sync`; reading needs only `vehicles.read` | `SourceAdministrationTests` |
| A7 | A user's muted sources narrow their own searches and nobody else's | `MySourcesTests` |
| A8 | Prices carry their incoterm, and one that does not is marked as not comparable | Vehicle card; `PricingAndDetailTests` |

A4 is the one worth checking by hand. It is the only guard against a requirement whose match
list looks plausible and is made of the wrong cars.

## B. Deduplication

Master prompt §3 does not name deduplication; [D3](02-decisions.md#d3--deduplication-strong-id-only-auto-merge)
and [D16](02-decisions.md#d16--near-duplicates-are-suggested-never-merged) do, and
[`09-poc-evaluation.md`](09-poc-evaluation.md) measured why it matters.

| # | Criterion | How to verify |
| --- | --- | --- |
| B1 | Two listings sharing a strong identifier attach to one vehicle | `VehicleSyncServiceTests` |
| B2 | A blank or placeholder identifier never matches another blank one | `CanonicalIdentityTests` |
| B3 | Near-duplicates are suggested for review and **never merged automatically** | `DuplicateDetectionTests` |
| B4 | A suggestion carries the reasons for it, not just a score | Duplicates screen; `SignalsJson` |
| B5 | A merge moves the listings, photos and alerts, and archives the duplicate | `DuplicateDetectionTests` |
| B6 | Every merge is reversible from the record it wrote | Merge history → Undo |
| B7 | A rejected pair is not suggested again | `A_rejected_pair_does_not_come_back_tomorrow` |
| B8 | Vehicles owned by different tenants are never paired | `Vehicles_owned_by_different_tenants_are_never_paired` |

## C. Customer CRM

Master prompt §3: *customer CRM, requirements, tags, notes, assignment and activity timeline*.
Tags, assignment and the timeline are **not built** — see [§X](#x-what-is-not-built).

| # | Criterion | How to verify |
| --- | --- | --- |
| C1 | A customer can be created, searched by name, phone or email, edited and deleted | `CustomerTests` |
| C2 | Deleting a customer removes their requirements, notes and alerts | `Deleting_the_customer_takes_their_notes` and siblings |
| C3 | Tenant A cannot read, edit or delete tenant B's customers by any route | `CustomerTests`, `CustomerNoteTests` |
| C4 | A requirement records what a customer wants, in the catalogue's own enums | `CustomerMatchingTests` |
| C5 | A requirement's matches are drawn from the catalogue and say which criteria applied | `The_response_says_which_criteria_were_applied` |
| C6 | Criteria stored but **not** filtered on say so rather than appearing to have applied | `matchedOn` includes "recorded, not filtered" |
| C7 | Notes are a dated log with an author, not one overwritable box | `CustomerNoteTests` |
| C8 | A note cannot be back-dated, and an edit keeps the original date and marks itself | `Editing_a_note_keeps_when_it_was_written_and_says_it_changed` |
| C9 | `customers.read` sees the book; `customers.manage` is needed to change it | `AuthorizationTests`, `CustomerNoteTests` |

## D. Import

Master prompt §3: *Excel/CSV vehicle and customer import with validation and error reporting*,
*XML/JSON/FTP feed support through the same abstraction*. Vehicle spreadsheet import, XML and
FTP are **not built** — see [§X](#x-what-is-not-built).

| # | Criterion | How to verify |
| --- | --- | --- |
| D1 | A JSON vehicle file imports through the same abstraction as a live provider | `VehicleImportTests` |
| D2 | A customer CSV imports with a dry run that writes nothing | `CustomerImportTests` |
| D3 | Malformed rows are reported by row and by name, not silently dropped | Import result `problems` |
| D4 | A re-imported sheet **skips** people already on the books rather than overwriting them | `Someone_already_on_the_books_is_skipped_not_overwritten` |
| D5 | The CSV reader handles quoted delimiters, embedded newlines and doubled quotes | `CsvReaderTests` |
| D6 | An import into a wrongly-typed source is refused with a readable reason | `VehicleImportTests` |
| D7 | Import needs `vehicles.sync` or `customers.manage` as appropriate | `AuthorizationTests` |

## E. Messaging

Master prompt §3: *WhatsApp Business, Facebook/Meta and Instagram messaging integrations where
permitted*. Only WhatsApp is built, and only as a click-to-chat link — see
[D15](02-decisions.md#d15--whatsapp-ships-as-a-click-to-chat-link-until-the-business-api-is-approved).

| # | Criterion | How to verify |
| --- | --- | --- |
| E1 | A message about a car is drafted and shown before anything leaves the platform | Messaging drawer |
| E2 | Nothing sends without a person pressing send | D15; there is no send path |
| E3 | A phone number that cannot be resolved **refuses** rather than guessing a country | `PhoneNumberTests` |
| ~~E4~~ | ~~The message carries no source link and no price~~ | **Superseded** — see below |
| E4a | `{Price}` resolves to the dealer's own retail price and never a source listing's | `TemplateFields`; `TemplateRendererTests` |
| E4b | A template offering the listing link warns what it does before it is saved | Template editor |
| E5 | No enum name reaches a message a customer reads | `A_multi_word_transmission_reads_as_the_trade_writes_it` |
| E6 | The screen says what will actually happen, not "sent" | `canSendDirectly` drives the wording |

**E4 was overridden by the product owner and is recorded rather than quietly dropped.** It was
written as an absolute: no price, no source link, because the source URL names the exporter and a
customer who follows it buys direct. When message templates were built the owner was asked
directly and chose to allow both, *"my call per template"* — which is a legitimate call for
somebody who knows their own customers and is the only person whose margin is at risk.

The commercial rule survives in the narrower form the two rows above state, and that half is not
discretionary. `{Price}` resolves from `TenantVehicle.TenantPrice` only: the catalogue row carries
what the **exporter** is asking, and rendering that would quote the dealer's own buying price —
their entire margin — to the person they are quoting to. An unset retail price drops the line
rather than falling back to anything.

This is the only criterion in A–F that a later decision has invalidated, which is worth saying
plainly: the rest of this document still describes the system as built.

## F. Alerting

Closes [O11](05-open-items.md#o11--saved-search-alerting).

| # | Criterion | How to verify |
| --- | --- | --- |
| F1 | A car arriving after a requirement was written raises an alert | `RequirementAlertTests` |
| F2 | Writing a requirement against existing stock raises **no** alerts | The `ListedAfterUtc` rule |
| F3 | Re-scanning raises nothing new | Unique index on (requirement, vehicle) |
| F4 | Only open requirements are scanned | `RequirementAlertTests` |
| F5 | An alert names the customer, the requirement and the car, with the price at match time | Alerts inbox |

F2 is the difference between a useful inbox and one nobody opens: without it, a new requirement
fires an alert per matching car — 46 of them against the real catalogue.

---

## G. What gates Phase 2

These were open when this document was drafted. Phase 2 is entirely about sending data to a
model, so the first is not deferrable; two of the four have since closed.

| # | Item | Why it blocks |
| --- | --- | --- |
| ~~G1~~ | ~~[O4](05-open-items.md#o4--pii-redaction-before-ai-calls) — PII redaction before AI calls~~ | **Closed** as [D20](02-decisions.md#d20--a-customers-message-is-redacted-before-it-leaves-and-never-stored). The owner chose: send it redacted, never store it. Both halves are enforced by construction — `Redaction` strips identifiers, `ExtractionRequest` cannot be built from a raw string, and the audit row holds measurements rather than content. Feature 1 had already sidestepped the question by sending a `RequirementBrief` with nowhere to put a name; feature 2 could not, so it was answered. |
| ~~G2~~ | ~~[O2](05-open-items.md#o2--carapis-licensing-gate) — Carapis licensing~~ | **Closed** — the owner decided against using Carapis, which answers the question by removing it. The fallback the open item named was never hypothetical: the whole Phase 0.5 evidence base came through the file import route, so nothing is lost. Two now-unusable sources remain in the seeder's bootstrap list, which reaches new databases only; see O2. |
| ~~G3~~ | ~~[O8](05-open-items.md#o8--publicid-coverage) — `PublicId` coverage~~ | **Closed** as [D17](02-decisions.md#d17--top-level-route-identifiers-are-guids-nested-ones-may-be-integers). Phase 1 first made this worse — two more integer-keyed routes — then settled it: top-level routes take a GUID, nested ones may keep an integer because the parent's GUID already gates them. Enforced by `RouteIdentifierTests`. |
| G4 | WhatsApp Business API approval | Four of Phase 2's seven features need *inbound* messages, which D15's click-to-chat path structurally cannot see. Weeks of lead time; nothing has been applied for. |

G3 was a defect this phase introduced and has since been fixed; it is left in the table struck
through rather than deleted, because a gate that was real and then closed is part of the record.
G1 was closed after this document was drafted and before it was signed.

**G4 alone remains open**, and it is the one nothing in this repository can close. Four of Phase
2's seven features need *inbound* messages, which D15's click-to-chat path structurally cannot
see. No application has been made, and the lead time runs in weeks from whenever one is.

## X. What is not built

Every master prompt §3 bullet not covered above, with the reason. None of these is an oversight;
each was either deferred on evidence or is blocked.

| §3 item | State | Reason |
| --- | --- | --- |
| Excel/CSV **vehicle** import | Not built | Deferred by the product owner: no real exporter spreadsheet exists to design column mapping against, and every exporter names columns differently. Guessing the shape first is how the wrong abstraction gets built. |
| XML and FTP feeds | Not built | No source requires them yet. `IVehicleSourceProvider` is the seam they arrive through; nothing above it names a transport. |
| Manual vehicle creation/editing | Not built | Deprioritised by the product owner — a broker sourcing from exporter feeds adds a car by hand rarely. |
| Customer **tags** | Not built | §3 asks for them; [`01-sql-schema.md`](01-sql-schema.md) has no column for them. That contradiction needs resolving before a shape is guessed at. |
| Customer **assignment** | Partial | `Customer.AssignedUserId` exists and is preserved across edits; no screen sets it. A single-operator business has nobody to assign to. |
| Activity timeline | Not built | Explicitly skipped by the product owner in favour of the note log, which is the half a person actually writes. A timeline is generated and answers "what happened"; notes answer "what did they say". |
| Facebook/Meta and Instagram messaging | Not built | Same approval path as WhatsApp Business, and none applied for. §3 says "where permitted". |
| Webhook synchronization, unified conversations | Not built | Both need inbound messages. D15's consequences section records why this is structural rather than unfinished. |
| Tasks and follow-up scheduling | Not built | Explicitly skipped by the product owner. |

## Sign-off

Phase 1 is accepted when A–F pass and §X is agreed as the deferral list. Record the date and who
verified it. **§G stays open regardless** — signing this does not clear the Phase 2 gates.

| Field | Value |
| --- | --- |
| Verified by | Claude Opus 5, by the method recorded below |
| Accepted by | gmhhashmi@gmail.com (product owner), who instructed this sign-off |
| Date | 2026-09-14 |
| Result | **Accepted**, with E4 superseded and §G still open |

### How this was verified, on the day it was signed

Recorded in this much detail because the section above already admits the criteria were written
after the work. A signature on a document like that is worth exactly what the checking behind it
was worth, so here is the checking.

**The whole suite, green.** 226 unit and 246 integration tests, 0 failures, 0 skips, against a
real SQL Server rather than an in-memory provider.

**Every test this document names by name still exists.** The criteria point at specific classes
and specific test methods, and a criterion pointing at a deleted test passes by being unverifiable.
All were enumerated from the built assemblies and found: `SearchFilterEndpointTests` (4),
`SourceAdministrationTests` (7), `MySourcesTests` (7), `PricingAndDetailTests` (6),
`VehicleSyncServiceTests` (9), `CanonicalIdentityTests` (12), `DuplicateDetectionTests` (16),
`CustomerTests` (13), `CustomerMatchingTests` (5), `CustomerNoteTests` (10),
`AuthorizationTests` (12), `VehicleImportTests` (12), `CustomerImportTests` (12),
`CsvReaderTests` (15), `PhoneNumberTests` (21), `MessageComposerTests` (10),
`RequirementAlertTests` (7), `MessagingTests` (11) — along with each individually named method,
including `A_rejected_pair_does_not_come_back_tomorrow`,
`Vehicles_owned_by_different_tenants_are_never_paired`,
`Someone_already_on_the_books_is_skipped_not_overwritten` and
`Editing_a_note_keeps_when_it_was_written_and_says_it_changed`.

**A4 by hand, which is the one this document says no test can do.** Against the live 499-vehicle
database, a requirement was saved with eight filters at once — make Toyota, model Aqua, years
2016 to 2021, mileage 30,000 to 110,000, hybrid, budget to 2,500 — and its match list compared
against the same eight typed into the vehicle search. Both returned the same three cars
(2016/97,320/533.33, 2017/64,455/933.33, 2020/63,153/1,733.33). This is the check that catches a
requirement field the UI never sends, which looks like a working match list made of wrong cars.

**C6 in its own words.** A requirement carrying a destination and a variant reported
`variant G` alongside `destination PK (recorded, not filtered)` — the criterion is that a stored
but unapplied filter says so rather than appearing to have worked, and it does.

**What was not re-verified today.** The live browser checks listed in the section above were done
when this document was written and were taken on that record rather than repeated: A2, A5, B4,
B6, C1, C7, C8, D2, E1, F5. Each is a rendering claim with a passing test behind the data it
renders. Anything whose behaviour has changed since — E4 — is recorded above rather than left to
be discovered.

**What signing this does not do.** §G stays open: [O4](05-open-items.md#o4--pii-redaction-before-ai-calls)
was since answered for Phase 2's first two features by
[D20](02-decisions.md#d20--a-customers-message-is-redacted-before-it-leaves-and-never-stored),
but G4 — WhatsApp Business API approval — has still not been applied for, and four of Phase 2's
seven features need it. Nothing here accepts §X as built; it accepts §X as the agreed list of
what is not.

### Evidence available at the time of writing

Suite at the time of drafting: **124 unit + 204 integration**, 0 failures, 0 skips, against a real
SQL Server. At sign-off it was 226 + 246; the growth is Phase 1 follow-ups and Phase 2 feature 1.

Verified live in a browser against a running instance rather than by reading code: A1–A5, A8,
B3–B7, C1, C5–C8, D2, E1, E4, F5. The duplicate detection in §B was measured against the real
104-listing corpus from [`09-poc-evaluation.md`](09-poc-evaluation.md): 14 pairs examined of
4,851 possible, 13 raised — the 12 documented duplicates plus one the evaluation had not found —
and 1 correctly declined.

Decisions taken during Phase 1:
[D15](02-decisions.md#d15--whatsapp-ships-as-a-click-to-chat-link-until-the-business-api-is-approved),
[D16](02-decisions.md#d16--near-duplicates-are-suggested-never-merged) and
[D17](02-decisions.md#d17--top-level-route-identifiers-are-guids-nested-ones-may-be-integers).
Open items closed: [O11](05-open-items.md#o11--saved-search-alerting),
[O15](05-open-items.md#o15--near-duplicate-detection-without-a-strong-identifier) and
[O8](05-open-items.md#o8--publicid-coverage).

### Known weakness in this document

It was written **after** the work rather than before it. Phase 0's criteria were set in advance
and the implementation had to meet them; these were derived from what exists, which is a weaker
guarantee — a criterion nobody thought to write is a criterion nothing fails. The §3 bullets in
[§X](#x-what-is-not-built) are the honest counterweight: they are the parts where the
specification asked for something and the answer is "no, and here is why".
