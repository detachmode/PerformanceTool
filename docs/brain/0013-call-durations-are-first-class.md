# 0013. Call durations are a first-class result

- Status: Accepted (user requirement). Recording decided; how it's shown is still open
- Date: 2026-11-20

## What
The UI usually shows one box/bar per **actor**. But what matters in the end is also **how long every call inside
each actor took** (terms: `docs/context.md`). So:
- The tool records the duration (and outcome) of **each call** within an actor, not only the actor total.
- The UI lets the user see call durations, both within one actor and across all actors of a scenario
  (e.g. "how long did `POST /cart` take over the run?").

**Recording (decided):** calls are recorded **automatically from `HttpClient`**, most likely through a
**`DelegatingHandler`** in the scenario's HTTP pipeline. It measures each request inside the current actor (and its
logging scope) without any extra code in the scenario.

**Naming/tagging a call (decided: on the request).** Scenario code names a call by attaching it to the
`HttpRequestMessage`, e.g. an extension that stores a name and optional tags in `request.Options`, which the handler
reads:
```csharp
await ctx.Http.SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = body }
    .AsCall("Place order", tags: new() { ["step"] = "payment" }), ct);
```
Exact API names are still open.

## Why
A scenario chains several calls. The actor total hides *which* call is slow or failing. The user needs
per-call timing to act on the results.

## Open (to decide with the user)
- ~~How it's shown~~ **Decided:** variant **A, stacked actor bars** (each actor bar split into call segments).
  The user finds it the best and most scalable; an actor can have **around 50 calls**, so the view must stay
  readable with that many segments. Variants B and C are dropped. Earlier options were:
  - (A) actor bars stacked by call segments
  - (B) small "box chart" cards per call, like the Aspire metric cards, under each scenario
  - (C) a matrix/heatmap of actors × calls
  - the per-actor waterfall already in draft 3
- Default name for untagged calls (e.g. method + path with IDs collapsed) and how calls are grouped.
- Whether an ambient block style (`using (ctx.Call("…"))`, for SDKs/typed clients) is added later as a secondary option.
