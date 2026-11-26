# 0067. Calls overview chart: typical (p50) vs. p95 horizontal bar comparison

- Status: Accepted, implemented
- Date: 2026-11-26

## What
Add a horizontal **Response Time by Endpoint (p50 vs. p95)** overview chart to `/calls` (`Calls/CallsOverviewChart.razor`):

**Accepted (user decisions)**
- **One row per endpoint**, sorted slowest-first by p95 by default (following `/calls`'s active search, scenario, outcome and sort controls).
- **Overlaid bar on a shared ms X-axis:**
  - **Solid inner bar** = typical duration (**p50**).
  - **Translucent outer bar** = **p95** (95% of calls faster), extending to the right of p50.
  - **Right-hand label:** `p50 / p95 ms` in tabular numerals (e.g. `114 / 292 ms`), plus quiet call/failure badges where relevant.
- **Autoscaled linear axis:** the horizontal ms axis autoscales to the maximum p95 among the visible endpoints (with clean round tick intervals, e.g. `0ms`, `100ms`, `200ms`, …).
- **Target reference line:** vertical dashed line across the chart. Uses the scenario's configured target / slow threshold when set, and **50 ms by default** when none is set (with an inline editable input to adjust on the fly, e.g. `?target=50`). Endpoints with their own per-endpoint `.SlowAbove(...)` threshold (brain 0060) can also show a subtle per-row tick.
- **Colour & failure indicator (brain 0018, 0041):**
  - **Bar colour** always reflects **latency** relative to the target: **green** when within target, shifting through **slight greenish-yellow** up to **amber** for very slow endpoints (consistent with brain 0041's graded slowness palette).
  - **Failures** are shown with a **red failure icon** (plus failed count badge) on the row — **not** by changing the bar's colour — so latency and failures remain independently readable at a glance (e.g. a fast endpoint with occasional failures still shows a green bar alongside its red failure icon).

**Implemented details**
- **Placement:** shown in the right pane of `/calls` (`CallsPage.razor`) by default when no endpoint is selected, with a pinned **Overview** entry at the top of the left sidebar (`SelectOverview`) so the user can return from any endpoint detail in one click. On phone (`PhoneState`), `/calls` gets an `Overview` tab alongside `Endpoints` and `Details` with stacked rows (`html.phone .cl-ov-*` in `app.css`).
- **Scale, target & colour logic:** `Calls/CallsOverviewScale.cs` resolves the target line (URL `?target=` override → scenario `SlowThresholds.Default` → shared custom threshold → `50 ms` default), computes nice linear X-axis ticks autoscaled to max p95 (and target), and maps `p95 / reference` ratios to `--good` → `--g1..--g3` → `--slow` (respecting per-endpoint `SlowThresholdMs` when set, ignoring failures for bar colour).
- **Interactive & drill-down (brain 0006, 0029):**
  - Hovering or focusing a row highlights it and shows a compact summary (`calls`, `p50`, `p95`, `max`, `slow`, `failed`, custom limit) in the header readout and row title.
  - Clicking any row opens that endpoint's detail view on `/calls` (`?endpoint=...`); clicking the row's red failure pill drills directly into that endpoint filtered to failed calls (`?endpoint=...&show=failed`).
- **Rendering:** plain HTML/CSS (`Calls/CallsOverviewChart.razor`) using `app.css` dark tokens and `--good` / `--g1..--g3` / `--slow` / `--failed` variables — no external charting library.

## Why
Today `/calls` lists endpoints as text on the left and inspects only one endpoint at a time on the right. Comparing relative latencies across all operations in a run — both **absolute slowness** and **tail spread** (e.g. a fast p50 of 25 ms with a 115 ms p95 tail) — requires reading numbers row by row. A shared-axis p50/p95 bar chart makes hotspots and tail latency obvious at a glance while preserving Beesharp's colour rule (red = failed, green-to-amber = slowness).

## Consequences
- Uses the existing `CallEndpointSummaryDto` data from `GET /api/runs/{runId}/calls/endpoints` and Fake mode `RunStore`; no storage, DuckDB schema, or REST contract changes are required.
- Keeps the main `/` Overview page calm (brain 0016) by housing the cross-endpoint chart on `/calls`.
- Works live while a run is in progress as well as on finished and stored runs.
