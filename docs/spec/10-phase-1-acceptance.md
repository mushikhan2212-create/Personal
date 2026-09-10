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
| E4 | The message carries no source link and no price | `MessageComposerTests` |
| E5 | No enum name reaches a message a customer reads | `A_multi_word_transmission_reads_as_the_trade_writes_it` |
| E6 | The screen says what will actually happen, not "sent" | `canSendDirectly` drives the wording |

E4 is a commercial rule, not a style choice: the source URL names the exporter, and a customer
who follows it buys direct.

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

These are open **after** this document is signed. Phase 2 is entirely about sending data to a
model, so the first is not deferrable.

| # | Item | Why it blocks |
| --- | --- | --- |
| G1 | [O4](05-open-items.md#o4--pii-redaction-before-ai-calls) — PII redaction before AI calls | Every Phase 2 feature sends customer data somewhere. Nobody has decided what may leave. |
| G2 | [O2](05-open-items.md#o2--carapis-licensing-gate) — Carapis licensing | Its own text says resolve **before Phase 1 starts**. It was not resolved and Phase 1 was built anyway. The architecture survives a "no"; the commercial exposure is real. |
| G3 | [O8](05-open-items.md#o8--publicid-coverage) — `PublicId` coverage | Some routes expose GUIDs, others sequential integers. Phase 1 **added two more** integer-keyed routes (`/customers/{id}/notes/{noteId}`, `/duplicates/{id}/merge`), making the inconsistency worse rather than better. |
| G4 | WhatsApp Business API approval | Four of Phase 2's seven features need *inbound* messages, which D15's click-to-chat path structurally cannot see. Weeks of lead time; nothing has been applied for. |

G3 is a defect this phase introduced. Either answer to O8 is defensible — the inconsistency is
the problem — but it should be settled before more routes are added.

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
| Verified by | _unset_ |
| Accepted by | _unset_ |
| Date | _unset_ |
| Result | _pending_ |

### Evidence available at the time of writing

Suite: **124 unit + 204 integration**, 0 failures, 0 skips, against a real SQL Server.

Verified live in a browser against a running instance rather than by reading code: A1–A5, A8,
B3–B7, C1, C5–C8, D2, E1, E4, F5. The duplicate detection in §B was measured against the real
104-listing corpus from [`09-poc-evaluation.md`](09-poc-evaluation.md): 14 pairs examined of
4,851 possible, 13 raised — the 12 documented duplicates plus one the evaluation had not found —
and 1 correctly declined.

Decisions taken during Phase 1:
[D15](02-decisions.md#d15--whatsapp-ships-as-a-click-to-chat-link-until-the-business-api-is-approved)
and [D16](02-decisions.md#d16--near-duplicates-are-suggested-never-merged). Open items closed:
[O11](05-open-items.md#o11--saved-search-alerting) and
[O15](05-open-items.md#o15--near-duplicate-detection-without-a-strong-identifier).

### Known weakness in this document

It was written **after** the work rather than before it. Phase 0's criteria were set in advance
and the implementation had to meet them; these were derived from what exists, which is a weaker
guarantee — a criterion nobody thought to write is a criterion nothing fails. The §3 bullets in
[§X](#x-what-is-not-built) are the honest counterweight: they are the parts where the
specification asked for something and the answer is "no, and here is why".
