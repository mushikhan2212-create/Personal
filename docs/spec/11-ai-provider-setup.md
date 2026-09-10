# Turning the AI ranking on

Everything for Phase 2 feature 1 is built and tested except the network call itself. This is what
to do when you have a key.

The feature works before you do any of this — it falls back to price order and says so on screen.

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
Take the id from the provider's own current documentation.

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
