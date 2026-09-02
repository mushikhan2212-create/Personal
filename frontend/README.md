# Car Dealer — search screen (Phase 0.5)

React 18 + Vite 6 + TypeScript 5.7 (strict) + Ant Design 5, per decisions D8 and D9.

## Running it

```bash
npm install
npm run dev          # http://localhost:5173
```

The dev server proxies `/api` to the backend, so the browser makes same-origin requests and
the API needs no CORS configuration.

### Pointing it at your backend

The proxy target defaults to `http://localhost:5080`, which is the port `docker compose up`
publishes. **`dotnet run` uses a different port** — its launch profile listens on `5246`. If
you start the API that way, tell the frontend:

```bash
cp .env.example .env.local     # then set VITE_API_URL=http://localhost:5246
```

Restart `npm run dev` after changing it — Vite reads the proxy config at startup only.

If in doubt, use the `Now listening on:` line the API prints when it starts. When the proxy
cannot reach the backend the dev server logs it explicitly, rather than leaving you with an
empty grid and no explanation.

## Signing in

The seeded development accounts all use the password `Dev_Passw0rd!`:

| Account | Role | Can sync |
| --- | --- | --- |
| `owner@nihon-motors.test` | Tenant owner | yes |
| `sales@nihon-motors.test` | Salesperson | no |
| `readonly@nihon-motors.test` | Read only | no |
| `owner@karachi-auto.test` | Tenant owner, second tenant | yes |
| `multi@example.test` | Member of both tenants | yes |

`multi@example.test` is the one that exercises the two-phase login decision D2 requires: it
asks which tenant to enter before issuing a token, because an access token is scoped to
exactly one tenant.

Signing in as the two owners in turn is the quickest way to see the catalog rules: both see
the shared global vehicles, neither sees the other's private inventory, and a tenant's own
price overlay is invisible to the other.

## Notes

- **Tokens are held in memory only**, never in `localStorage`, so a page reload signs you out.
  That is deliberate: a bearer token in `localStorage` is readable by any script on the page.
- An expired access token is renewed automatically. Refreshes share one in-flight promise
  because the API rotates the refresh token and treats a replay as theft — two concurrent
  refreshes would revoke the whole chain.
- `src/api/types.ts` is hand-mirrored from the API's OpenAPI document. Generating it from
  `/swagger/v1/swagger.json` is the intended end state; until then it has to be kept in step
  with the controllers by hand.
