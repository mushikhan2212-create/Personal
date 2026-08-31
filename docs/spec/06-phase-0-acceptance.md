# Phase 0 — Acceptance Criteria

Closes open item [O9](05-open-items.md#o9--phase-0-acceptance-criteria). Master prompt §8 gives
testable criteria for the Carapis POC; Phase 0 had none. This is the equivalent.

Under [D10](02-decisions.md#d10--phase-0-is-backend-only-swagger-is-the-test-surface) Phase 0
ships **no frontend**, so every criterion below is verifiable through the Swagger page, a shell
command, or the test suite. "It looks right" is not available as a check, which is why these are
written as pass/fail rather than as goals.

**Phase 0 is complete when every item passes.** Phase 0.5 does not begin before then.

---

## A. Build and run

| # | Criterion | How to verify |
| --- | --- | --- |
| A1 | Solution builds with no warnings-as-errors suppressed | `dotnet build` |
| A2 | `docker compose up` starts SQL Server, Redis and the API | `docker compose up -d`, then A3 |
| A3 | API answers on its published port and Swagger renders | Open `/swagger` |
| A4 | Frontend is **absent** by design | No `frontend/` project; D10 |
| A5 | A clean clone runs with no manual steps beyond documented env vars | Fresh clone → `docker compose up` → `/swagger` |

## B. Migrations and seed

Master prompt §16 requires migrations for all schema changes; schema §12 requires migration to be
tested from empty and from the previous version.

| # | Criterion | How to verify |
| --- | --- | --- |
| B1 | Migrations apply to an **empty** database | Drop DB, `dotnet ef database update` |
| B2 | Migrations apply from the **previous** version | Apply to the prior migration, then update |
| B3 | Every Phase 0 table from [`04-schema-delta.md`](04-schema-delta.md#8--phasing) exists | Inspect schema |
| B4 | No table is created outside the Phase 0 set | Schema §10 — the POC minimum, plus D7's additions |
| B5 | Seed is deterministic and re-runnable without duplicating rows | Run seed twice, compare row counts |
| B6 | Seed creates the fixture in [§S](#s-seed-fixture) below | Query `Tenants`, `Users`, `TenantUsers`, `Roles` |

## C. Multi-tenancy and isolation

The core of Phase 0, and the hardest thing to verify without a UI. Schema §9 requires automated
proof; [`04-schema-delta.md` §1.4](04-schema-delta.md#14-query-filter-and-isolation-tests) lists
the four cases.

| # | Criterion | How to verify |
| --- | --- | --- |
| C1 | Every tenant-owned query is scoped from the authenticated token, never from a request parameter | Code review + C2 |
| C2 | A client-supplied tenant identifier is **ignored or rejected**, never trusted | Call an endpoint with a forged tenant id in body/header/query; expect no cross-tenant data |
| C3 | Tenant A cannot read tenant B's records | Automated test; also by hand via Swagger with both tokens |
| C4 | Tenant A cannot update or delete tenant B's records | Automated test |
| C5 | A user with membership in two tenants sees only the active tenant's data | Log in as the dual-membership user (§S), switch, compare |
| C6 | Switching tenants requires an explicit endpoint and issues a new token | Swagger; old token must not silently change scope |
| C7 | A tenant switch writes an `AuditLogs` row | Switch, then query `AuditLogs` |
| C8 | Suspending a membership in one tenant does not lock the user out of the other | Set `TenantUsers.MembershipStatus = Suspended` for one tenant, log in |

C2 and C5 are the two most likely to fail quietly. Test them by hand even though they are covered
by automated tests.

## D. Authentication and token revocation

Master prompt §14 requires secure password/token handling and a token revocation strategy.

| # | Criterion | How to verify |
| --- | --- | --- |
| D1 | Passwords stored with a modern adaptive hash, never reversible | Inspect `Users.PasswordHash`; confirm algorithm and work factor |
| D2 | Login returns an access token and a refresh token | Swagger |
| D3 | Access token carries the active `TenantId` | Decode the JWT |
| D4 | Refresh rotates the token and invalidates the previous one | Refresh twice; the first refresh token must now fail |
| D5 | Reusing a revoked refresh token fails **and** revokes the whole chain | Reuse an already-rotated token; confirm the chain is dead |
| D6 | Logout revokes the refresh token | Log out, then attempt refresh |
| D7 | `RefreshTokens.TokenHash` stores a hash, never the token itself | Inspect the column |
| D8 | Expired access tokens are rejected | Wait for expiry or shorten it in config |

D5 is the one that distinguishes a real revocation strategy from a token that merely expires.

## E. Authorization

Master prompt §3 requires users, roles **and permissions**.

| # | Criterion | How to verify |
| --- | --- | --- |
| E1 | Permissions are data (`Permissions`/`RolePermissions`), not hard-coded role-name checks | Code review |
| E2 | An endpoint requiring a permission rejects a user without it | Call as `ReadOnly` user; expect 403 |
| E3 | 403 (authorized user, insufficient permission) is distinct from 401 (unauthenticated) | Compare both responses |
| E4 | Roles can be tenant-scoped; two tenants may each define a role with the same name | Create "Sales Manager" in both tenants |
| E5 | System roles (`Roles.TenantId IS NULL`) cannot be edited or deleted by a tenant | Attempt as tenant owner |
| E6 | A user's permissions resolve per active tenant, not globally | Dual-membership user with different roles per tenant |

## F. API contract

| # | Criterion | How to verify |
| --- | --- | --- |
| F1 | API is versioned and the version appears in the route or header | Swagger |
| F2 | Validation failures return a consistent, documented error contract | Post an invalid payload |
| F3 | Unhandled exceptions return the same contract, with no stack trace in non-development | Trigger one with the environment set to Production |
| F4 | Error responses carry the correlation ID | Any 4xx/5xx response |
| F5 | Health checks report database and cache status | `GET /health` |
| F6 | Health endpoint distinguishes liveness from readiness | `/health/live`, `/health/ready` |
| F7 | OpenAPI document is complete: every endpoint has parameters, responses and auth requirements | Read `/swagger/v1/swagger.json` |

F7 matters more than usual — under D10 this document **is** the product surface for Phase 0, and
it is what the Phase 0.5 frontend will generate its types from
([D8](02-decisions.md#d8--typescript-on-the-frontend)).

## G. Logging, correlation and audit

| # | Criterion | How to verify |
| --- | --- | --- |
| G1 | Logs are structured, not string-concatenated | Inspect log output |
| G2 | Every log entry carries tenant, user and correlation context where available | Make an authenticated request, read the logs |
| G3 | A correlation ID is generated per request and returned to the caller | Response header |
| G4 | A supplied correlation ID is honored rather than replaced | Send one, confirm it propagates |
| G5 | Critical actions write `AuditLogs` rows: login, logout, tenant switch, role change, permission change, user invite/suspend | Perform each, query the table |
| G6 | **No secret, password, token or connection string appears in any log** | Grep the log output for known seed secrets |

G6 is a hard fail, not a warning. Master prompt §14 states it twice.

## H. Infrastructure abstractions

Master prompt §5 requires that business logic never call vendor SDKs directly.

| # | Criterion | How to verify |
| --- | --- | --- |
| H1 | Caching is behind an interface; Redis is one implementation | Code review |
| H2 | In-memory cache fallback works and is **development-only** | Run without Redis in Development; confirm Production refuses to start |
| H3 | File/media storage is behind an interface with a local implementation | Code review |
| H4 | Background jobs run behind a replaceable abstraction | Code review; enqueue a job |
| H5 | A queued job survives an API restart | Enqueue, restart, confirm it still runs |
| H6 | No vendor SDK type appears in Application or Domain projects | Inspect project references |

H2's Production check is deliberate: a silent in-memory fallback in production looks healthy while
losing every cache entry per instance.

## I. Security

| # | Criterion | How to verify |
| --- | --- | --- |
| I1 | No secret is committed; configuration comes from environment/secret store | `git log -p` scan; review `appsettings*.json` |
| I2 | Authentication and public endpoints are rate-limited | Exceed the limit; expect 429 |
| I3 | Integration credentials are encrypted at rest where feasible | Inspect `VehicleSourceConfigurations.CredentialReference` handling |
| I4 | HTTPS enforced outside local development | Check the Production profile |
| I5 | All data access is parameterized (EF Core or explicit parameters) | Code review for string-concatenated SQL |
| I6 | File upload endpoints, if any exist in Phase 0, validate size and type | Attempt an oversized/wrong-type upload |

## J. Tests

| # | Criterion | How to verify |
| --- | --- | --- |
| J1 | `dotnet test` passes with zero failures and zero skips | `dotnet test` |
| J2 | Cross-tenant isolation tests exist and cover all four cases in [`04-schema-delta.md` §1.4](04-schema-delta.md#14-query-filter-and-isolation-tests) | Read the test names |
| J3 | Token rotation and revocation-chain tests exist | Read the test names |
| J4 | Permission enforcement tests exist | Read the test names |
| J5 | Integration tests run against a real SQL Server, not only an in-memory provider | Inspect the test fixture |
| J6 | Assignment guard is tested: a customer cannot be assigned to a non-member ([`04-schema-delta.md` §2.3](04-schema-delta.md#23-assignment-guard)) | Read the test names |
| J7 | Test suite runs in CI configuration without manual setup | Run the CI command locally |

J5 matters because the in-memory provider does not enforce the constraints Phase 0 depends on —
unique indexes, filtered indexes and computed columns all behave differently or not at all.

## K. Documentation

Master prompt §16 requires setup, environment variables, migrations, provider configuration and
troubleshooting to be documented.

| # | Criterion | How to verify |
| --- | --- | --- |
| K1 | README covers prerequisites, first run, and how to reach Swagger | Follow it on a clean clone |
| K2 | Every environment variable is documented with purpose and whether it is required | Read the docs |
| K3 | Migration commands documented, including creating a new migration | Read the docs |
| K4 | Seed fixture documented, including credentials for local testing | Read the docs |
| K5 | Troubleshooting covers the common local failures: SQL Server not ready, port conflicts, expired tokens | Read the docs |
| K6 | `docs/database/erd.mmd` matches the implemented Phase 0 schema | Compare |

---

## S. Seed fixture

Under D10 the seed is the only way to exercise multi-tenancy by hand, so it is a Phase 0
deliverable in its own right rather than a convenience. It must be deterministic (schema §12) and
re-runnable without duplicating rows.

**Tenants**

| Slug | Name | Default currency |
| --- | --- | --- |
| `nihon-motors` | Nihon Motors | JPY |
| `karachi-auto` | Karachi Auto Imports | USD |

**System roles** — `TenantId IS NULL`: `TenantOwner`, `Admin`, `SalesManager`, `Salesperson`,
`ReadOnly`.

**Users**

| Email | Membership |
| --- | --- |
| `owner@nihon-motors.test` | Nihon Motors — TenantOwner |
| `sales@nihon-motors.test` | Nihon Motors — Salesperson |
| `readonly@nihon-motors.test` | Nihon Motors — ReadOnly |
| `owner@karachi-auto.test` | Karachi Auto — TenantOwner |
| `multi@example.test` | **Both** — Admin in Nihon Motors, ReadOnly in Karachi Auto |
| `suspended@example.test` | **Both** — Active in Nihon Motors, Suspended in Karachi Auto |

The last two users exist specifically to make [D2](02-decisions.md#d2--multi-tenant-user-membership)
testable. `multi@example.test` proves permissions resolve per tenant rather than globally (C5, E6);
`suspended@example.test` proves per-tenant suspension does not lock a user out everywhere (C8).
Without them, multi-tenant identity is untested.

Local passwords are documented in the README (K4) and must be development-only. Seeding these
users must be impossible in a Production environment.

---

## Verification status

Recorded when Phase 0 was implemented. This is the developer's own run, not the sign-off —
the sign-off below is yours.

Re-run in full after the move to .NET 10
([D11](02-decisions.md#d11--net-10-not-net-8)): 41 tests and all 29 live checks pass on
`net10.0`, with the EF Core 8 migration requiring no changes.

**Verified automatically.** 41 tests pass (13 unit, 28 integration) across three consecutive
runs, against a real SQL Server. The integration suite covers C2–C8, D2–D8, E1–E6, F4, G4 and
the correlation-id handling. Migrations apply to an empty database on every test run (B1), the
seed is idempotent across restarts (B5), Production refuses to start without Redis (H2), the
OpenAPI document exposes 13 operations all carrying responses and a Bearer scheme (F7), and no
password, connection string, signing key or refresh token appears in any log (G6).

**Container images.** Both Dockerfiles build, and the published API image was run against SQL
Server: it migrated, seeded, answered `/health`, served a login, and runs as the non-root `app`
user (uid 1654). The build had to be run with this sandbox's TLS-intercepting proxy CA injected,
because NuGet is otherwise unreachable from inside a container here — that injection is a local
workaround and is deliberately **not** in the committed Dockerfile, which needs no such thing on
a normal network.

**Two items could not be fully verified in the development environment**, and both need
checking on your machine:

1. **B2 — migration from the previous version.** Only one migration exists, so there is no
   previous version to migrate from. This becomes testable at the first schema change; CI
   already guards the related risk by failing when the model and migrations disagree.
2. **The Redis-backed cache path.** The Redis image could not be pulled here (blocked at the
   Docker Hub CDN), so `DistributedCacheService` ran only in the in-memory configuration.
   H1 and H2 are verified — the abstraction round-trips and Production refuses to boot without
   Redis — but the Redis implementation itself has not executed. `docker compose up` should
   exercise it; confirm `/api/v1/diagnostics/cache-roundtrip` reports
   `"implementation": "DistributedCacheService"`.

Items resting on judgement rather than a test — H6 (no vendor SDK in Application or Domain),
I5 (no concatenated SQL), G1 (structured logging) — were checked by reading the code, and the
project files enforce H6 structurally: `CarDealer.Domain` declares no package references at all.

## Sign-off

Phase 0 is accepted when A–K and S all pass. Record the date and who verified it, then Phase 0.5
begins against [`04-schema-delta.md` §8](04-schema-delta.md#8--phasing).

| Field | Value |
| --- | --- |
| Verified by | Claude Code — automated suite plus live checks against a running instance |
| Accepted by | gmhhashmi@gmail.com, who reviewed the evidence and delegated the sign-off |
| Date | 2026-08-31 |
| Result | **Accepted**, with the four exceptions recorded below |

### Evidence

`dotnet build` in Release with `TreatWarningsAsErrors`: 0 warnings, 0 errors. `dotnet test`:
49 tests, 0 failures, 0 skips, against a real SQL Server, run twice consecutively.

Checked live against a running instance rather than by reading code: B1 (migrations onto an
empty database), B5/B6 and §S (seed idempotent across four restarts; fixture exact), C2 (a
forged tenant id in the query string and two headers, ignored), C5–C8, D1 (PBKDF2-HMAC-SHA512,
100,000 iterations, 16-byte salt), D3–D6, E2–E4, E6 (the dual-membership user resolves six
permissions in one tenant and one in the other), F2, F4–F7, G4, H1, H2, H5, I1, I2, I4.

G6 was re-run explicitly: five secret patterns, zero occurrences across 191 log lines.

### Exceptions carried into Phase 0.5

1. **B2 — now closed.** It could not be tested at sign-off time because only one migration
   existed. The Phase 0.5 catalog migration supplied the missing previous version, and
   `InitialPhase0` then `Phase05Catalog` have since been applied in sequence to an empty
   database.

2. **Redis (H1) — closed on evidence, unconfirmed on the accepting party's machine.** The
   `DistributedCacheService` path had never executed anywhere. It has now been verified against
   a real Redis 7.0.15: `cache-roundtrip` reports `DistributedCacheService` with
   `matched: true`, and the entry is present in Redis as `cardealer:<key>` with the expected
   TTL. On the accepting party's own machine the check still reports `InMemoryCacheService`,
   because Docker is not installed there. Confirm locally before any deployment.

3. **H5 — was a real defect, fixed before sign-off.** A job whose worker died mid-execution
   stayed invisible for Hangfire's 30-minute default. `SlidingInvisibilityTimeout` is now set
   in both hosts; recovery measured at 303 seconds against a reproduction of the original
   failure.

4. **I3 and I6 are not applicable to Phase 0.** `VehicleSourceConfigurations` is deferred by
   D7 and arrives in Phase 0.5, and Phase 0 exposes no upload endpoint. I3 becomes live now
   that the Carapis credential exists — see
   [`07-carapis-api.md`](07-carapis-api.md#credential-handling).

Reproduce any of the above with [`scripts/phase0-verify.sh`](../../scripts/phase0-verify.sh).
