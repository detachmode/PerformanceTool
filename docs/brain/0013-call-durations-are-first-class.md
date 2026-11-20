# 0013. Call durations are a first-class result

- Status: Accepted (user requirement); how to record and show it is still open
- Date: 2026-11-20

## What
The UI usually shows one box/bar per **instance**. But what matters in the end is also **how long every call inside
each instance took** (terms: `docs/context.md`). So:
- The tool records the duration (and outcome) of **each call** within an instance, not only the instance total.
- The UI lets the user see call durations, both within one instance and across all instances of a scenario
  (e.g. "how long did `POST /cart` take over the run?").

## Why
A scenario chains several calls. The instance total hides *which* call is slow or failing. The user needs
per-call timing to act on the results.

## Open (to decide with the user)
- **How it's shown.** Draft options to compare:
  - (A) instance bars stacked by call segments
  - (B) small "box chart" cards per call, like the Aspire metric cards, under each scenario
  - (C) a matrix/heatmap of instances × calls
  - the per-instance waterfall already in draft 3
- **How calls are recorded.** Explicitly in scenario code (e.g. a `context.Call("name", …)` helper), automatically
  from `HttpClient` requests, or both.
- How calls are named and grouped (e.g. per step name vs per URL).
