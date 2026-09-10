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

Three options, any of which works. **Never `appsettings.json` or `appsettings.Development.json`**:
those are committed, and a key in git history is permanent.

```bash
# backend/.env  — already git-ignored
AI__Provider=groq
AI__Model=<the model id, exactly as the provider spells it>
AI__ApiKey=<your key>
```

The double underscore is how .NET nests configuration: `AI__Provider` sets `AI:Provider`.

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
3. **Speed and cost**, last on purpose. Groq is fast and cheap across its range, so these rarely
   separate two models that both pass — and a cheaper model with a higher rejection rate is not
   cheaper.

Prefer a larger general instruction model over a small one. The saving on a task this size is
fractions of a cent, and small models fail rule 1 more often.

Take the id from Groq's own current documentation or from the probe's discovery output; the ids
change, which is exactly why this file names none.

## Sizing it to your rate limit

The measurement that decides whether this works at all, and the one nobody thinks to take.

A Groq `on_demand` account is capped at **1,000 output tokens per minute**, and the cap is
enforced against what a request *asks for* rather than what it uses — the request is rejected
outright with a 429, not truncated. Measured on the probe's five-car set, a ranking costs about
**190 output tokens per car**. So:

| Cars sent | Output tokens | Fits 1,000 OTPM? |
| --- | --- | --- |
| 5 | ~950 | just |
| 10 | ~1,900 | no |
| 20 (the default) | ~3,800 | no |

**On a rate-limited tier, leave the default and every ranking 429s and falls back to price
order.** It is safe — the screen says so — but the feature never actually runs. Set it to what
your tier affords:

```
AI__MaxCandidates=5
```

Five cars is still a shortlist and still worth ordering. Raise it when the tier does; the code
clamps anything above 40, because a typo in a config file should not send the whole catalogue to
a model.

**Which five it sends is chosen by fit, not by price.** The catalogue query fetches up to the
clamp of 40, then the cars comfortably inside every limit are taken first. Otherwise a dealer on
a small tier would send the five cheapest — which, on a requirement with a mileage ceiling, is
often the five nearest to it.

`AI__MaxTokens` (default 8,000) is the other half of the same arithmetic. Some models reject a
value above their own context ceiling — one on this account caps at 4,096 — so lower it if a
model you want returns a 400 naming `max_tokens`.

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
