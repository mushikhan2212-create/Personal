# Turning the AI ranking on

Everything for Phase 2 feature 1 is built and tested except the network call itself. This is what
to do when you have a key.

The feature works before you do any of this — it falls back to price order and says so on screen.

## What the model does *not* decide

One rule about the ordering is applied in code rather than asked of the model. A car that only
just satisfies a limit you set — within a tenth of the mileage ceiling, or exactly at the oldest
year accepted — is placed after the cars that sit comfortably inside, even when it is cheaper.
Within each of those two groups the model's own ordering stands untouched, and if nobody named a
ceiling nothing moves at all.

This holds whether or not a provider is configured, so the fallback list and the ranked list mean
the same thing. The reasoning, the trade-offs, and the one case where you might want it changed
are in [D19](02-decisions.md#d19--a-preference-the-broker-states-is-code-not-a-sentence-in-the-prompt).

## 1. Put the key somewhere it will never reach git

**Never `appsettings.json` or `appsettings.Development.json`**: those are committed, and a key in
git history is permanent. Never `backend/.env.example` either — that one is committed too, and
the name makes it look safe.

The settings are the same whichever way you run the API:

```bash
AI__Provider=groq
AI__Model=<the model id, exactly as the provider spells it>
AI__ApiKey=<your key>
AI__MaxCandidates=8
AI__MaxTokens=1200
```

The double underscore is how .NET nests configuration: `AI__Provider` sets `AI:Provider`.

**Where they go depends on how you start the API, and the two are not interchangeable.**

| How you run it | Where the settings go |
| --- | --- |
| `docker compose up` | `backend/.env` — git-ignored, read by compose |
| `dotnet run` | exported in your shell, or `dotnet user-secrets` |

`backend/.env` is read by **docker compose**, not by .NET. Compose interpolates it into
`docker-compose.yml`, which then passes named variables into the container — so a variable has to
be listed in the `api` service's `environment:` block to reach the application at all. The
`AI__*` ones are listed there. If you add a new setting, add it there too, or it will be set
correctly and have no effect.

For `dotnet run` the file is not read at all. Export them, or keep them out of the filesystem
entirely:

```bash
cd backend/src/CarDealer.Api
dotnet user-secrets set "AI:ApiKey" "<your key>"
dotnet user-secrets set "AI:Provider" "groq"
dotnet user-secrets set "AI:Model" "<the model id>"
```

Note the single colon: that form is the configuration path itself, not the environment-variable
spelling. Both forms bind to the same settings.

Those commands write one file, which you can equally write by hand. The project's
`UserSecretsId` is **`cardealer-api-phase0`**, so it lives at:

| OS | Path |
| --- | --- |
| Windows | `%APPDATA%\Microsoft\UserSecrets\cardealer-api-phase0\secrets.json` |
| Linux / macOS | `~/.microsoft/usersecrets/cardealer-api-phase0/secrets.json` |

```json
{
  "AI": {
    "Provider": "groq",
    "Model": "<the model id>",
    "ApiKey": "<your key>",
    "MaxCandidates": 8,
    "MaxTokens": 1200
  }
}
```

**User secrets live outside the repository, which is the whole point** — they cannot be committed
by accident, by the CLI or by the GitHub web editor.

They are read only when the environment is Development, which every launch profile sets. They are
also read only by the process that has that `UserSecretsId`: the API, which is the only thing that
ranks. The worker has its own id and needs none of this.

**Do not set both this and `backend/.env`.** They feed different ways of starting the API, and
keeping two copies of a key is how one of them goes stale and costs you an afternoon.

**Check it took.** Rank any requirement and look at the response, or the screen: if
`providerConfigured` is false, the application never saw the key, whatever the file says.

For Groq you can leave `AI__BaseUrl` unset — it defaults to `https://api.groq.com/openai/v1`.
For anything else OpenAI-shaped, set it to that endpoint.

For Anthropic:

```bash
AI__Provider=anthropic
AI__Model=<the model id>
AI__ApiKey=<your key>
```

**There is deliberately no default model.** Lineups change faster than this code will, and a
stale default that silently resolves to a retired model is worse than being told to name one.

## Choosing a model

Don't pick one from a datasheet. The only thing that decides it is whether a model can do *this*
task, and that takes about a minute per model to find out:

```bash
cd backend/tools/CarDealer.ModelProbe
AI__ApiKey=<your key> dotnet run
```

With no arguments it asks your account what it serves, drops the ones that could never rank a car
(speech, embedding, moderation), and runs each survivor through **the application's own prompt and
its own guards** against a deliberately awkward five-car set — the cheapest has the highest
mileage, the newest is dearest, one is missing its colour, and two are close enough that the
ordering is a judgement rather than a sort.

It prints, per model: whether the answer survived the guards, how long it took, tokens in and out,
and **the top three of its actual ordering with the reasons it gave**.

Cars marked `[near a limit]` only just satisfy something the customer set — within a tenth of the
mileage ceiling, or at the oldest year they accepted. Those are placed after the ones that do not
by the application, not by the model ([D19](02-decisions.md#d19--a-preference-the-broker-states-is-code-not-a-sentence-in-the-prompt)),
so judge a model on the order **within** each group, and on whether its reasons mention the flag
at all.

To check specific models instead:

```bash
AI__ApiKey=<key> dotnet run -- --model <id> --model <id>
```

### Probe two or three at a time, not all of them

The output-token ceiling is **per minute and shared across the whole run**. A sequential probe
spends the allowance on the first models and 429s the rest — which reads exactly like a verdict
on those models and is not one. It has already happened here: three consecutive runs struck off
both qwen models for being third and fourth in an alphabetical list, and one of them turned out
to be the cheapest usable model on the account.

So either probe a couple at a time, or space the run out:

```bash
AI__ApiKey=<key> dotnet run -- --model <id> --model <id> --delay 60
```

A 429 is now labelled in the output as a rate limit rather than a failure. Re-probe anything that
hits one, alone.

### What decides it

**Structured output support is the whole game.** The adapter sends
`response_format: {type: "json_schema", strict: true}`. A model that honours it returns something
the parser accepts; one that ignores it answers in prose, gets rejected, and falls back to price
order *every single time*. The probe surfaces this immediately as "The response was not valid
JSON".

After that, in order:

1. **Instruction-following**, which shows up as guard failures — dropped candidates, invented ids,
   figures the car never stated. The probe counts these for you.
2. **The ordering itself**, which no test can judge. Read the top three the probe prints and ask
   whether you would have shown those cars in that order. This is the half that needs you.
3. **Output tokens per car**, which turns out not to be last at all on a rate-limited tier. It is
   a property of the *model*, not of the task, and it varies more than threefold between models
   that produce the same ordering — see the measurements below. On a 1,000 OTPM account it is
   what decides how many cars you can send.

Take the id from Groq's own current documentation or from the probe's discovery output; the ids
change, which is exactly why this file names none.

### What the probe found on this account

Measured 2026-09-10 against `rank-v6`, five cars, `--max-tokens 2000`. **This is a snapshot, not
a recommendation with a shelf life** — re-run the probe when the lineup changes.

| Model | Verdict | Time | Output tokens | Per car |
| --- | --- | --- | --- | --- |
| `qwen/qwen3.8-27b` | OK | 2,184 ms | 416 | ~83 |
| `openai/gpt-oss-20b` | OK | 3,036 ms | 949 | ~190 |
| `openai/gpt-oss-120b` | OK | 3,618 ms | 1,521 | ~304 |
| `qwen/qwen3.6-27b` | no | — | — | `json_validate_failed`, three runs running |
| `allam-2-7b` | no | — | — | does not support `json_schema` |
| `groq/compound` | no | — | — | does not support `json_schema` |
| `groq/compound-mini` | no | — | — | does not support `json_schema` |

**All three usable models returned the same ordering.** That is the band split doing its job: the
question the models still answer — how to order cars that are genuinely comparable — is one they
agree on here, so what separates them is cost and the quality of the reasons.

On reasons, the smallest model wrote the best ones. `qwen/qwen3.8-27b` gave *"3 sources list it;
Lowest mileage of the group at 65,803 km"*, where `gpt-oss-120b` opened all five entries with
*"under the 8,000 budget"* — true of every candidate by construction, because the filter already
guaranteed it, and so a wasted slot out of three. **Ignore the advice to prefer a bigger model
that used to be in this section: on this task it was wrong**, and it would have cost 3.7× the
output tokens to be wrong with.

Two blemishes worth knowing about, neither caught by a guard and neither worth a prompt revision
on its own: qwen wrote *"Hybrid fuel type"* and gpt-oss-120b wrote *"meets the minimum year"*,
both of which are closer to naming a field than rule 8 asks for.

## Sizing it to your rate limit

The measurement that decides whether this works at all, and the one nobody thinks to take.

A Groq `on_demand` account is capped at **1,000 output tokens per minute**, and a request that
would exceed it is rejected outright with a 429 rather than truncated.

**The cap is applied to an estimate Groq computes, which is neither `max_tokens` nor what the
answer actually uses.** An earlier version of this file said it was enforced against what the
request asks for; the evidence does not support that. At `max_tokens` 8,000 the same five-car
request was refused with *"Limit 1000, Requested 1387"* for one model and *"Requested 2390"* for
another — nowhere near 8,000, and different per model. Lowering `max_tokens` to 2,000 brought
both under. So `max_tokens` clearly feeds the estimate without equalling it, and **the 429 body
tells you the number it computed**, which is the only reliable way to size this.

The budget is also **per minute across every request in it**, not per request. Two rankings in
quick succession share it.

What that means in practice:

1. Take the model's measured output-per-car from the table above.
2. Set `AI__MaxCandidates` so the total sits well under 1,000, with room to spare.
3. Set `AI__MaxTokens` a little above that total — high enough that a long answer is not
   truncated mid-JSON, low enough that Groq's estimate fits.
4. Watch `AIRequests` for a week. A 429 shows up there as `Failed`; if none appear, raise it.

For `qwen/qwen3.8-27b` at roughly 83 output tokens per car, that is:

```
AI__MaxCandidates=8
AI__MaxTokens=1200
```

For `openai/gpt-oss-20b` at roughly 190, the same budget buys four or five cars. **The model you
choose decides how long your shortlist can be** — which is the argument for the cheap one that
happens to rank identically.

Whatever you set, the code clamps candidates above 40, because a typo in a config file should not
send the whole catalogue to a model.

**Which cars it sends is chosen by fit, not by price.** The catalogue query fetches up to the
clamp of 40, then the cars comfortably inside every limit are taken first. Otherwise a dealer on
a small tier would send the cheapest — which, on a requirement with a mileage ceiling, is often
the ones nearest to it.

Some models also reject a `max_tokens` above their own context ceiling — `allam-2-7b` on this
account caps at 4,096 — so lower it if a model you want returns a 400 naming `max_tokens`. That
is a different failure from the 429 above, and the message says which.

## 2. Restart the API

The provider is chosen at startup:

- `AI:Provider` = `anthropic` → the official Anthropic SDK.
- anything else with a key and a base URL → the OpenAI-compatible adapter, which is what Groq
  speaks.
- no provider or no key → `UnconfiguredAIProvider`, and the screen explains that.

## 3. Try it

Open a customer, expand a requirement with matches, press **Rank with AI**.

## What you should expect the first time

Both adapters compile and **neither has ever run against a live endpoint**, because no key existed
when they were written. Two failures are likely, and both are visible rather than silent:

**A model id the account cannot reach.** Arrives as a 400 whose body names it. The screen shows
price order with the provider's message.

**Structured output ignored.** `response_format: json_schema` is honoured by some models and
ignored by others. A model that ignores it returns prose, the parser rejects it, and you get price
order with "The response was not valid JSON". If that happens, the fix is a model that supports
structured outputs — not a change to the guards.

## Where to look when it does not work

The screen tells you what happened, but the detail is in the database:

```sql
SELECT TOP 20 Provider, Model, Status, FailureReason, Cost, CreatedAtUtc
FROM AIRequests ORDER BY Id DESC;
```

`Status` distinguishes the two kinds of bad:

| Status | Meaning |
| --- | --- |
| `Succeeded` | Answered and believed. |
| `Failed` | No answer — network, timeout, refusal, unparseable. |
| `Rejected` | Answered, and a guard threw it away. `FailureReason` says which. |

**`Rejected` is the number that matters when choosing between providers.** It counts answers you
paid for and could not use. A provider with a low price and a high rejection rate is not cheap.

## Comparing Anthropic against Groq

The reason the guards exist. Point the config at one, rank a handful of real requirements, then
point it at the other and do the same. Compare:

- the rejection rate from the query above,
- `Cost` and token usage per call,
- and whether the orderings look right to you, which no test can answer.

Change `AI__Provider`, `AI__Model` and `AI__ApiKey`, restart, and nothing else in the system needs
to know.

## What this feature sends

Only the structured requirement and the candidate cars. **No name, no phone number, no email, no
notes, no free text** — `RequirementBrief` has no field for any of them, which is why this could
ship without waiting on [O4](05-open-items.md#o4--pii-redaction-before-ai-calls).

If you later use AI on customer messages, that decision has to be made properly first, and made
again for whichever provider holds the text.
