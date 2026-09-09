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
| `manager@nihon-motors.test` | nihon-motors | SalesManager |
| `readonly@nihon-motors.test` | nihon-motors | ReadOnly |
| `owner@karachi-auto.test` | karachi-auto | TenantOwner |
| `multi@example.test` | **both** | Admin in nihon-motors, ReadOnly in karachi-auto |
| `suspended@example.test` | **both** | Active in nihon-motors, **Suspended** in karachi-auto |

The last two exist to make multi-tenant identity testable at all: `multi@example.test` proves
permissions resolve per tenant rather than globally, and `suspended@example.test` proves a
suspension in one tenant does not lock the user out of another.

### Measuring the catalogue

`GET /api/v1/catalog-report` computes what master prompt §8 asks the POC to report — field
completeness per source, freshness, deduplication effectiveness, image coverage, and five timed
searches — from whatever is currently imported. It needs `vehicles.sync`.

```bash
curl -s http://localhost:5246/api/v1/catalog-report \
  -H "Authorization: Bearer $TOKEN" | python -m json.tool
```

It is computed rather than written down because a number typed into a document is stale the
moment the next import runs. [`docs/spec/09-poc-evaluation.md`](../docs/spec/09-poc-evaluation.md)
is written from its output — including the POC's central finding, that twelve cars in a real
104-vehicle catalogue are listed by both exporters and the platform matches none of them.

### Who can do what

| | Search the catalogue | My Sources | Register, import, sync, delete sources |
| --- | --- | --- | --- |
| TenantOwner, Admin | yes | yes | yes |
| SalesManager, Salesperson, ReadOnly | yes | yes | **no** |

Registering a source or importing a file publishes cars into the shared catalogue that every
tenant reads, so it needs `vehicles.sync`, held by Admin and Tenant Owner only. Everyone else
can search, open a vehicle, and choose which of those sources feed their own searches — that
needs nothing beyond `vehicles.read`.

`manager@nihon-motors.test` is the account to check that with: signed in as Sales Manager the
**Import vehicles** button is absent from the header, the **My sources** screen shows only the
on/off switches with no Sync or Delete beside them, and the endpoints behind all of those
answer 403.

Everything to do with sources now lives on **My sources**. The search screen carries the
filters and the results and nothing else — the sources panel that used to sit above them was
noise on the screen people spend all day on, and it also contradicted the switches by showing
listing counts for sources the viewer had turned off.

The **Filters** drawer offers make, model, body, steering, fuel, transmission, year range,
mileage range and price range — which covers every criterion a saved requirement is actually
matched on. That parity is the point rather than a coincidence: a requirement's match list is
only checkable if a person can type the same filters by hand and get the same cars back.
(A requirement's *variant* goes through the free-text box, which is where the matcher sends it
too. Its colour and destination country are stored but never filtered on — the `matchedOn`
array in the match response is the server's own account of what it applied, and it says so.)

Make and model are separate from the free-text box because they mean different things:
"corolla" typed in the search box also matches a variant string that mentions it, which is
right when browsing and wrong when a customer has asked for a Corolla.

Grants are reconciled on startup from `Permissions.SystemRoleGrants`, so narrowing a system
role there takes effect on an existing database the next time the API boots. Roles a tenant
defines itself are never touched by that.

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

> **Source list empty?** The API seeds a starting set only into a catalogue that has **no
> sources at all**, so a fresh database gets them on first start. Once you have registered or
> deleted anything, the list is yours and the seeder never touches it again — deleting a sample
> source is permanent, and your own sources are never renamed or re-typed behind you. To add
> one, use the **Register a source** button on the import screen; anything registered there is
> DealerJson and ready to import into.

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

## Messaging a customer on WhatsApp

`POST /api/v1/messaging/whatsapp/draft` composes a message about a car and returns a
click-to-chat link. In the app it is the **WhatsApp** button on an alert row, on a customer, and
on a vehicle — the last opens with a customer picker, since there the car is known and the
person is not.

```bash
curl -X POST http://localhost:5246/api/v1/messaging/whatsapp/draft \
  -H "Authorization: Bearer <access token>" -H "Content-Type: application/json" \
  -d '{"customerPublicId":"<guid>","vehiclePublicId":"<guid>"}'
```

**Nothing is sent by the platform.** The link opens WhatsApp on the salesperson's own device
with the message typed in, and they press send. Replies go to their phone, not to the app — so
there is no inbox and no conversation history yet. Both arrive with the WhatsApp Business API,
which needs Meta Business verification; see
[D15](../docs/spec/02-decisions.md#d15--whatsapp-ships-as-a-click-to-chat-link-until-the-business-api-is-approved)
for why this is the interim shape and what changes when that approval lands. The response says
which is in force:

| Field | Now | With the Business API |
| --- | --- | --- |
| `canSendDirectly` | `false` | `true` |
| `canReceive` | `false` | `true` |
| `handoffUrl` | the wa.me link | null |

Screens read those flags rather than assuming, so the button can say "Open WhatsApp" today and
"Send" later without being rewritten.

**A number that cannot be placed is refused, not guessed.** `+92 300 1234567` needs nothing
from us. `0300 1234567` is resolved only when the customer's country is set and known — because
a link built on a guessed country code opens a chat with a real person who is not your
customer. When it refuses, the drawer says what to fix on the customer record.

Passing `body` sends that text instead of the composed one; the screen does this on every edit
so the link always matches what is on screen.

**The message carries no URL and no price.** This dealer brokers other exporters' stock, and a
source listing link names the supplier — a customer who follows it can buy direct. The price is
out for a softer version of the same reason: a quote is a conversation, not an opening line.

**Photos are attached, not linked.** A click-to-chat link carries text only, so a photo cannot
travel inside the message however it is encoded — and the usual workaround, putting the image
URL in the text, names the exporter exactly as the listing link would. So the draft returns the
car's photos alongside the message and the compose screen offers them for download:

```
GET /api/v1/vehicles/{publicId}/photos/{index}
```

The salesperson saves them and attaches them in WhatsApp with the paperclip; the customer
receives a picture with no address on it. The route proxies rather than redirects, because a
browser ignores `download` on a cross-origin link — and it takes an index rather than a URL, so
it reads the address out of our own rows and cannot be pointed anywhere else. Sending the image
itself becomes possible with the Business API, which supports an image message with a caption.

## New-match alerts

When a car is added to the catalogue **after** a customer has said what they are looking for,
an alert appears in the **New matches** screen and on the bell in the header. This is open item
[O11](../docs/spec/05-open-items.md#o11--saved-search-alerting): stock in this trade moves fast
and the dealer who calls first usually gets the sale.

A background job scans hourly. To check immediately — after an import, say — use the **Check
now** button, or:

```bash
curl -X POST http://localhost:5246/api/v1/alerts/scan \
  -H "Authorization: Bearer <access token>"
```

Two rules make the inbox worth reading, and both are load-bearing:

**An alert is about new stock, not about the catalogue.** A requirement written today against a
hundred cars typically matches dozens of them, and alerting on those would produce forty
notifications about cars already on the requirement's own matches list. So an alert is raised
only where the listing was first seen *after* the requirement was created.

**Scanning twice does not alert twice.** A unique index on (requirement, vehicle) means an
alert exists once, ever. Running the scan by hand while the hourly job is also running costs a
query, not a duplicate inbox.

Only requirements with status `Open` are scanned — telling somebody about stock for a customer
who has already bought is how an inbox stops being read. Alerts deliberately ignore per-user
muted sources: a mute is a browsing preference, and letting one salesperson's preference
suppress a colleague's alert would lose a sale for a reason nobody could see afterwards.

`customers.read` sees alerts; `customers.manage` is needed to mark them seen or to trigger a
scan, because in a shared inbox clearing an alert tells colleagues it has been dealt with.

## Importing customers from a spreadsheet

`POST /api/v1/customers/import` takes a CSV as multipart form data and needs `customers.manage`.
The **Import CSV** button on the Customers screen is the same endpoint.

```bash
# Always dry-run first: it reports exactly what a real import would do and writes nothing.
curl -X POST "http://localhost:5246/api/v1/customers/import?dryRun=true" \
  -H "Authorization: Bearer <access token>" -F "file=@contacts.csv"
```

The first row must be a header. Column names are matched after lower-casing and stripping
punctuation, so `First Name`, `first_name` and `FIRSTNAME` are one column:

| Field | Headings understood |
| --- | --- |
| First name | first name, first, given name, forename |
| Last name | last name, last, surname, family name |
| Either | name, full name, customer name — split on the first space |
| Phone | phone, mobile, cell, whatsapp, contact number |
| Email | email, e-mail address, mail |
| City | city, town |
| Country | country, country code — must be a two-letter code |
| Status | status — one of Unknown, Lead, Active, Customer, Dormant, Closed |
| Source | source, lead source, came from |
| Language | language, preferred language |
| Notes | notes, note, comment, comments, remarks |

Anything else is ignored rather than rejected. Quoted fields may contain commas and line
breaks, semicolon-delimited exports are detected, and the byte order mark Excel writes is
stripped — without that the first heading never matches and the file looks column-less.

**A customer already on the tenant's books is skipped, not overwritten.** An import is usually
a re-import of the same sheet a month later, and overwriting would discard the notes, status
and assignment a salesperson has edited since. Duplicates are matched on the phone number
(punctuation ignored) or the email (case ignored), against both the database and rows already
accepted from the same file. Each skipped row is listed by name so it can be reconciled by hand.

A row that cannot be read is reported and the rest still import — one bad row out of four
hundred must not cost the other 399. The limits are 5,000 rows and 8 MB per request.

Unlike a vehicle import, the uploaded file is **not** stored: it is parsed and discarded. A
spreadsheet of names and phone numbers should not sit in blob storage while
[O3](../docs/spec/05-open-items.md#o3--pii-and-data-protection) is unanswered.

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
