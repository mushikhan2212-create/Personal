# Car Dealer — search screen (Phase 0.5)

React 18 + Vite 6 + TypeScript 5.7 (strict) + Ant Design 5, per decisions D8 and D9.

## Running it

```bash
npm install
npm run dev          # http://localhost:5173
```

Start the backend first — in Visual Studio, press F5 (the `http` or `https` profile; either
works). Then open http://localhost:5173.

### Why the network tab shows localhost:5173

Requests appear as `POST http://localhost:5173/api/v1/auth/login` — the dev server's own
origin, not the API's port. **That is correct.** The page calls its own origin and Vite
forwards everything under `/api` to the backend server-side. The browser never talks to the
API directly, which is why the backend needs no CORS configuration.

The consequence worth knowing: **a 500 from localhost:5173 usually means the proxy could not
reach the backend**, not that the backend rejected your request. The dev server's console says
which — it prints the target it is using at startup, and an explicit message when it cannot
connect.

### Setting your backend port

The port is at the top of `vite.config.ts`, as `DEFAULT_API_URL`:

| How you start the API | Port |
| --- | --- |
| **Visual Studio, F5** | **5246** — the default |
| `docker compose up` | 5080 |

Take it from the `Now listening on: http://localhost:____` line the API prints at startup.
Change it in `vite.config.ts`, or set `VITE_API_URL` in `.env.local` to avoid editing a
tracked file:

```bash
cp .env.example .env.local
```

**Restart `npm run dev` after changing either** — Vite reads its config once, at startup. The
startup banner then confirms the target:

```
  API proxy: /api -> http://localhost:5246
```

Use the `http://` address even when Visual Studio also starts an `https://` listener. The
https port serves a self-signed certificate the proxy would reject, and Development does not
redirect http to https, so plain http is both simpler and fully working.

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
