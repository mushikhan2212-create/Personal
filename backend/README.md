# Car Dealer SaaS — Backend (Phase 0)

Foundation for the multi-tenant dealer platform: tenancy, authentication, authorization,
auditing, and the infrastructure abstractions the later phases build on.

Built on **.NET 10 / EF Core 10** — see [decision D11](../docs/spec/02-decisions.md#d11--net-10-not-net-8),
which amends the master prompt's .NET 8 choice because .NET 8 leaves support in November 2026.

**Phase 0 ships no frontend.** Under [decision D10](../docs/spec/02-decisions.md#d10--phase-0-is-backend-only-swagger-is-the-test-surface)
the OpenAPI page and the test suite are the verification surface. The React application
arrives in Phase 0.5 with the first vertical slice.

Specifications live in [`docs/spec/`](../docs/spec/). The checklist this phase is signed off
against is [`06-phase-0-acceptance.md`](../docs/spec/06-phase-0-acceptance.md).

---

## Prerequisites

- .NET SDK 10.0
- Docker (for SQL Server and Redis)

## First run

```bash
cd backend
docker compose up -d --build
```

Then open **<http://localhost:5080/swagger>**.

On startup the API applies migrations and seeds reference data plus, outside Production, the
development fixture below.

### Running from Visual Studio, or the CLI

The usual debugging path. Start the dependencies only, then press F5 in Visual Studio (or run
the CLI command below):

```bash
docker compose up -d sqlserver redis
dotnet run --project src/CarDealer.Api
```

**This path serves on a different port.** Visual Studio and `dotnet run` both use the launch
profiles in `Properties/launchSettings.json`, which listen on **`http://localhost:5246`** (the
`https` profile adds `https://localhost:7241`) — not the `5080` that `docker compose up`
publishes. The frontend defaults to 5246 for exactly this reason; see `frontend/README.md` if
you change it.

Point the frontend at the **http** address even when the https listener is also running: its
certificate is self-signed and Development does not redirect http to https, so plain http is
the simpler working choice.

**Both dependencies must be running.** Unlike `docker compose up`, which injects connection
strings as environment variables, this path reads them from `appsettings.Development.json` —
which points `Redis` at `localhost:6379`, the port compose publishes.

Confirm the cache is actually backed by Redis rather than the development fallback:

```bash
curl -X POST 'http://localhost:5246/api/v1/diagnostics/cache-roundtrip?key=k&value=v' \
  -H "Authorization: Bearer <access token>"
```

`"implementation"` must read **`DistributedCacheService`**. If it reads
`InMemoryCacheService`, no Redis connection string was resolved and the cache is per-process
and non-shared — fine for a quick local run, but it means the Redis path is not being
exercised at all (acceptance criteria H1, H2).

## Logging in

Every seeded account uses the password **`Dev_Passw0rd!`**. These accounts are created only
outside Production.

| Email | Tenants | Role |
| --- | --- | --- |
| `owner@nihon-motors.test` | nihon-motors | TenantOwner |
| `sales@nihon-motors.test` | nihon-motors | Salesperson |
| `readonly@nihon-motors.test` | nihon-motors | ReadOnly |
| `owner@karachi-auto.test` | karachi-auto | TenantOwner |
| `multi@example.test` | **both** | Admin in nihon-motors, ReadOnly in karachi-auto |
| `suspended@example.test` | **both** | Active in nihon-motors, **Suspended** in karachi-auto |

The last two exist to make multi-tenant identity testable at all: `multi@example.test` proves
permissions resolve per tenant rather than globally, and `suspended@example.test` proves a
suspension in one tenant does not lock the user out of another.

In Swagger:

1. `POST /api/v1/auth/login` with `{"email": "...", "password": "Dev_Passw0rd!"}`.
   A user in several tenants gets `requiresTenantSelection: true` and the list of choices —
   call again including `tenantSlug`.
2. Click **Authorize** and paste the `accessToken` (no `Bearer ` prefix).
3. `POST /api/v1/auth/switch-tenant` moves to another tenant; it issues a **new** token,
   because a token is scoped to exactly one tenant.

## Environment variables

Configuration binds with `__` as the section separator, so `ConnectionStrings__Default`
overrides `ConnectionStrings:Default`.

| Variable | Required | Purpose |
| --- | --- | --- |
| `ConnectionStrings__Default` | yes | SQL Server connection string |
| `ConnectionStrings__Redis` | outside Development | Redis connection. **Absent outside Development is a startup failure** — the in-memory fallback is development-only, and silently degrading in production would look healthy while losing every cache entry per instance. In Development an absent *or empty* value selects the in-memory cache silently, so `appsettings.Development.json` sets `localhost:6379` to keep the local run on the same code path as every other environment |
| `Jwt__SigningKey` | yes | Token signing key, minimum 32 characters. Never commit it |
| `Jwt__Issuer` / `Jwt__Audience` | no | Default to `cardealer-api` / `cardealer-client` |
| `Jwt__AccessTokenMinutes` | no | Default 15 |
| `Jwt__RefreshTokenDays` | no | Default 14 |
| `RateLimits__Auth__PermitLimit` | no | Auth requests per window per IP. Default 10; Development uses 200 |
| `RateLimits__Auth__WindowSeconds` | no | Default 60 |
| `Carapis__ApiKey` | no | Vehicle source credential. Absent, the platform runs normally with synchronization disabled: the sync endpoint answers 503 and search over already-synced data is unaffected. See below |
| `Carapis__BaseUrl` | no | Default `https://api.carapis.com` |
| `Storage__Local__RootPath` | no | Local file storage root. Default `./storage` |
| `ASPNETCORE_ENVIRONMENT` | no | `Development`, `Staging`, `Production` |

Rate limiting partitions by remote IP, so a whole office behind one NAT address shares a
bucket. Deployments behind a proxy should raise the limit and rely on the proxy's own.

### Supplying the vehicle source API key

The key is a credential. It must never go in `appsettings*.json` or anywhere else that is
committed — those files are in the repository, and a key in git history stays there.

**Visual Studio (the usual path).** Right-click the **CarDealer.Api** project → **Manage User
Secrets**. Visual Studio opens a `secrets.json` stored in your user profile, outside the
repository, where it cannot be committed by accident. Add the key:

```json
{
  "Carapis": {
    "ApiKey": "your-key-here"
  }
}
```

The flat form works identically if you prefer it — `"Carapis:ApiKey": "your-key-here"` — but do
not put both in the same file. Restart debugging afterwards; configuration is read at startup.

Secrets are loaded **only in Development**, which is what the launch profiles set. That is
deliberate: staging and production must supply the key from their own secret store, never from
a developer's machine.

The CLI equivalent, if you would rather not use the IDE:

```bash
dotnet user-secrets --project src/CarDealer.Api set "Carapis:ApiKey" "your-key-here"
```

**`docker compose up`** — put it in `backend/.env`, which compose reads automatically and
`.gitignore` excludes. Copy `backend/.env.example` and fill in `CARAPIS_API_KEY`.

**A one-off run** — an environment variable, which lasts only for that shell. Note the
separator differs: `__` for an environment variable, `:` for user secrets. Both bind to the
same setting.

```powershell
$env:Carapis__ApiKey = "your-key-here"    # PowerShell
```

```bash
export Carapis__ApiKey="your-key-here"    # bash
```

#### Checking it was picked up

`POST /api/v1/vehicle-sources/sbtjapan/sync?maxPages=1` and read the response:

| Response | Meaning |
| --- | --- |
| **503**, "No vehicle source provider is configured" | The key did not reach the app. Wrong file, wrong separator, or the app was not restarted |
| **200** with `"status": "Succeeded"` | Working. `created` and `updated` say how many vehicles landed |
| **200** with `"status": "Failed"` and a network error in `errorMessage` | The key was read and the request was made; something between you and the API failed |
| **403** | Your account lacks `vehicles.sync`. Sign in as a tenant owner or sales manager |

Sync is off by default and that is a supported state: without a key the platform runs
normally, the sync endpoint answers 503, and search over already-synced data is unaffected.

Rotate the key if it has ever been pasted into a chat window, a ticket, or a commit.

## Trying it end to end

Fifteen minutes from a clean clone to a searchable catalogue of 400 cars. Every number below
is what you should actually see.

**1. Start the dependencies.** Only SQL Server and Redis run in Docker; the API runs from
Visual Studio.

```bash
cd backend
docker compose up -d sqlserver redis
```

**2. Run the API.** Press F5 in Visual Studio, or `dotnet run --project src/CarDealer.Api`.
It migrates and seeds on startup. Watch for `Now listening on: http://localhost:5246` — that
port matters in step 3.

**3. Run the frontend.**

```bash
cd frontend
npm install
npm run dev
```

It prints `API proxy: /api -> http://localhost:5246`. If your API printed a different port,
set `VITE_API_URL` in `frontend/.env.local` and restart.

**4. Sign in** at http://localhost:5173 as `owner@nihon-motors.test` / `Dev_Passw0rd!`
(pre-filled). The catalogue is empty — nothing has been imported yet.

> **Source list empty?** The API seeds its sources at startup, and the frontend hot-reloads
> while the backend does not — so after pulling, restart the API. Failing that, the import
> screen has a **Register a source** button; anything you register there is DealerJson and
> ready to import into.

**5. Import the sample data.** Click **Import vehicles**, choose source **Exporter A**, pick
`docs/spec/examples/import-exporter-a.json`, and click **Check without importing** first:

> would create 240 · 21 with no VIN, chassis or lot number · nothing written

Then **Import**. Now repeat with **Exporter B** and `import-exporter-b.json`:

> created 160 · **merged with existing 25**

Those 25 are the same physical cars offered by both exporters, matched on their chassis
numbers. That number is the whole point of the exercise.

**6. Look at the catalogue.** Back on search you should see **363 cars** — not 400, because 25
merged and the rest were marked unavailable by the file. Then:

- **Filter by price.** Under $5,000 gives 327; $5,000–$15,000 gives 35. These only work
  because the FX step converted every JPY price and pinned the rate it used (decision D6).
- **Sort by price**, low to high and high to low. Both were meaningless before that step.
- **Find an aggregated car** — a card reading *"2 offers from 2 sources — cheapest shown"*.
  Click it. The detail page lists both exporters' offers side by side, and the bottom panel
  says which identifier they were matched on.
- **Click a car with no identifier.** That same panel says plainly that it cannot be merged
  with the same car from another source. With Japanese stock this is common, and the screen
  says so rather than implying a judgement was made.

The sample photos point at `picsum.photos`, so they need internet access to render. Everything
else works offline.

## Importing vehicles from a file

The platform does not fetch from exporter websites (decision D13). It accepts data, and where
that data came from is your decision — an authorized partner feed, a dealer's export, or a tool
you run yourself.

The format is documented in [docs/spec/08-import-format.md](../docs/spec/08-import-format.md),
with a worked file at `docs/spec/examples/import-sample.json`.

```bash
# Always dry-run an unfamiliar file first: it reports exactly what a real import would do
# and writes nothing.
curl -X POST "http://localhost:5246/api/v1/vehicle-sources/{code}/import?dryRun=true" \
  -H "Authorization: Bearer <access token>" \
  -F "file=@docs/spec/examples/import-sample.json"

# Then import for real.
curl -X POST "http://localhost:5246/api/v1/vehicle-sources/{code}/import" \
  -H "Authorization: Bearer <access token>" \
  -F "file=@docs/spec/examples/import-sample.json"
```

`{code}` is a `VehicleSources.Code`. A source called **`file-import`** is seeded in every
environment so the commands above work immediately. To register your own:

```bash
curl -X POST http://localhost:5246/api/v1/vehicle-sources \
  -H "Authorization: Bearer <access token>" -H "Content-Type: application/json" \
  -d '{"code":"beforward","name":"BE FORWARD","providerType":"DealerJson","isShared":true}'
```

Two fields on that row decide things the import file cannot override. `providerType` must be
`DealerJson` — it selects the adapter that reads the payloads, and importing to a Carapis-typed
source such as `sbtjapan` returns 400. `isShared` decides whether the vehicles land in the
shared global catalog every tenant reads, or stay private to the calling tenant.

### Limiting what a source may ingest

Master prompt §18 forbids unlimited ingestion without filters. Put an allow-list in
`VehicleSources.IngestionFilterJson`:

```json
{
  "makes": ["Toyota", "Nissan"],
  "models": ["Hiace", "Land Cruiser", "X-Trail"],
  "destinationMarkets": ["PK", "KE"],
  "minYear": 2012,
  "maxRecords": 5000
}
```

An absent or empty list means no restriction on that dimension — never "allow nothing".
Excluded records are counted and returned as `skippedOutOfScope` rather than dropped silently.

## Migrations

```bash
export CARDEALER_MIGRATIONS_CONNECTION='Server=localhost,1433;Database=CarDealer;User Id=sa;Password=Dev_L0cal_Pass!2024;TrustServerCertificate=True;Encrypt=False'

# Create a migration after changing an entity or configuration
dotnet ef migrations add <Name> \
  --project src/CarDealer.Infrastructure \
  --startup-project src/CarDealer.Api \
  --output-dir Persistence/Migrations

# Apply
dotnet ef database update --project src/CarDealer.Infrastructure --startup-project src/CarDealer.Api

# Confirm the model and the migrations agree (CI runs this)
dotnet ef migrations has-pending-model-changes --project src/CarDealer.Infrastructure --startup-project src/CarDealer.Api
```

The API applies pending migrations on startup, so `database update` is only needed when
working without running the API.

## Tests

```bash
docker compose up -d sqlserver     # integration tests need a real SQL Server
dotnet test
```

Integration tests deliberately do **not** use the EF in-memory provider: it ignores unique
indexes, filtered indexes and persisted computed columns, and the tenant-scope uniqueness this
phase depends on is built from exactly those. Each test class provisions its own database and
drops it afterwards.

Override the server with `CARDEALER_TEST_SQL_HOST` and `CARDEALER_TEST_SQL_PASSWORD`.

## Project layout

```
src/
  CarDealer.Domain          entities and enums; no package references by design
  CarDealer.Application     abstractions and contracts; no vendor SDKs
  CarDealer.Infrastructure  EF Core, auth, caching, storage, jobs
  CarDealer.Integrations    external provider adapters (empty until Phase 0.5)
  CarDealer.Api             HTTP surface, middleware, composition root
  CarDealer.Worker          dedicated background job processor
tests/
  CarDealer.UnitTests       pure logic, no database
  CarDealer.IntegrationTests real SQL Server, full HTTP pipeline
```

`Domain` and `Application` reference no vendor SDK, which is what keeps master prompt §5's
"business logic must never call vendor SDKs directly" true rather than aspirational.

## How tenancy works

- A tenant is resolved **only** from the validated access token. A tenant id in a header,
  query string or body is ignored.
- EF Core global query filters scope every tenant-owned entity. When no tenant is resolved the
  comparison value is `0`, which matches nothing — an unresolved request sees no data rather
  than all of it.
- The auth path uses `IgnoreQueryFilters()` in a few explicit places (membership lookup,
  permission resolution) because it runs before a tenant exists. Those are scoped by an
  explicit `UserId`/`TenantId` predicate and covered by tests.
- Visibility is not mutability: system roles are readable by every tenant but writable by
  none. Phase 0.5's global vehicle catalog uses the same rule.

## Troubleshooting

**`Login failed for user 'sa'` / `Cannot open database`** — SQL Server is still starting.
`docker compose ps` should show `sqlserver` as healthy; the first cold start takes ~30s.

**API exits at startup with `No Redis connection string is configured`** — expected outside
Development. Set `ConnectionStrings__Redis`.

**`RedisConnectionException: UnableToConnect on localhost:6379`** — Redis is not running, and
in Development the API now points at it rather than silently falling back. Start it with
`docker compose up -d redis`, or `redis-server --port 6379` if you have Redis installed
locally. Only cache calls fail; the rest of the API is unaffected, because nothing caches at
startup.

**`cache-roundtrip` reports `InMemoryCacheService`** — no Redis connection string resolved.
Under `docker compose up` check that the `redis` service is healthy; running from the CLI,
check that `ConnectionStrings:Redis` in `appsettings.Development.json` is non-empty and that
no `ConnectionStrings__Redis=` environment variable is overriding it with a blank value.

**`OptionsValidationException` for `JwtOptions`** — `Jwt__SigningKey` is missing or shorter
than 32 characters.

**429 from `/api/v1/auth/*`** — the rate limiter. Wait for the window or raise
`RateLimits__Auth__PermitLimit`.

**401 on an endpoint that should work** — the access token is 15 minutes by default. Use
`POST /api/v1/auth/refresh`. Note that presenting an already-rotated refresh token revokes the
entire chain by design, so you will need to log in again.

**Port 1433 or 5080 already in use** — stop the conflicting container
(`docker rm -f cardealer-sql`) or change the published port in `docker-compose.yml`.
