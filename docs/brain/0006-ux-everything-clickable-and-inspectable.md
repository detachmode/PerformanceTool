# 0006. UX principle: everything is clickable, inspectable, filterable

- Status: Accepted (requirement; partly implemented, see `docs/plan.md` → "Interactive UX")
- Date: 2026-11-20

## What
The UI must be highly interactive with a really good UX. As a rule, **every element that represents data
can be clicked to see more about it, used as a filter, and isolated**:

- **Inspect**: clicking a bar, a scenario, a stat tile, a table row or a legend entry opens details
  (e.g. a side panel): for an execution its timing, status, error, bytes, start time and scenario
  parameters; for a scenario or stat, the executions behind the number.
- **Filter**: anything that has a category (scenario, status code, success/failure, latency range,
  time window) can be clicked to filter the chart, stats and tables. Active filters show as removable chips.
- **Isolate**: show only one thing (e.g. one scenario), or hide one, with a click. Legend entries toggle,
  and a modifier-click (or an "only" button) isolates.
- Filters and selections are consistent across views. The chart, stat tiles and breakdown table always
  describe the same filtered set.

## Why
The point of the tool is to *understand* performance, not just to produce a number. Finding out why p95 is
high means drilling into which scenario, which requests, which errors and when. If data can only be read
and not explored, users have to export it somewhere else.

## Consequences
- New UI that shows data must come with its click/inspect/filter behaviour. Don't ship read-only data views.
- Keep data in a shape that's easy to filter: every `ExecutionResult` carries its entry, status, error and
  start time. Stats are computed from the filtered set (`LatencyStats.From(...)`).
- Interactions need visible affordances (hover states, cursor, tooltips) and keyboard access (focusable
  elements, Esc closes panels), and they must keep working while a run is live.
- Colour still follows the entity (0002): filtering must never repaint the remaining scenarios.
