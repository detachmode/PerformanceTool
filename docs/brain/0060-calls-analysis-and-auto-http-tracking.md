# 0060. Calls analysis page (`/calls`), automatic HTTP tracking for all clients, and per-call slow thresholds

- Status: Accepted, implemented (user request)
- Date: 2026-11-24

## What
1. **New `/calls` page (`Pages/CallsPage.razor`, `Calls/`):**
   - 2-column master-detail layout focused on **endpoints and call durations** rather than bees:
     - **Left:** search, scenario filter, outcome chips (All / Slow / Failed), sortable endpoint list showing typical (median), slowest 5% (p95), max, slow count, failed count, total calls, and any author-configured slow limit.
     - **Right:** endpoint header with active slow rule pill and clickable KPIs; ad-hoc `Over X ms` filter + removable filter chips; **duration-over-time chart** (`CallDurationChart` + `wwwroot/call-chart.js`, horizontal drag-to-zoom, typical & slow threshold lines, bounded buckets on huge runs); clickable **latency distribution histogram** (`CallHistogram`); **selected call inspector** (exact raw URL with copy, timing, step, and direct links to the Bee and Logs); and virtualised **call list** (`CallListPanel`, slowest first by default).
   - A bee's Calls tab (`BeePanel`) also links each call directly to `/calls?endpoint=...`.
   - Shared aggregation engine in `Beesharp.Contracts` (`CallAnalytics`) used by DuckDB (`DuckDbRunStorage`), `InMemoryRunStorage`, and Fake mode (`RunStore.AllKeptCalls`), served via `GET /api/runs/{runId}/calls/endpoints` and `/calls/detail`.
   - Phone layout (`PhoneState`, `TabBar.razor`): 5th `Calls` tab in the bottom tab bar and `Endpoints` / `Details` phone tabs on `/calls`; desktop top bar compacts cleanly when `Assistant` is open and phone top bar restores the `.asst-toggle` button.
   - AI assistant integration (`Beesharp.Ai`): `list_call_endpoints`, `get_call_endpoint`, and `show_slow_calls` tools plus `ViewKinds.Calls` chat card linking directly to `/calls`.

2. **Automatic HTTP tracking for every `HttpClient` (`HttpCallTracking.cs`):**
   - In addition to `RecordingHttpHandler` on `ctx.Http`, `ScenarioContext.Activate()` installs `HttpCallDiagnosticTracker` subscribing once to `HttpHandlerDiagnosticListener` (`System.Net.Http.HttpRequestOut.Start` / `.Stop` / `.Exception`), automatically measuring `new HttpClient()` and custom `DelegatingHandler` chains whenever `ScenarioContext.Current` is active.
   - `BeesharpAppBuilder` also registers `RecordingHttpMessageHandlerBuilderFilter` (`IHttpMessageHandlerBuilderFilter`) so every DI typed/named/Refit `HttpClient` (including custom mock primary handlers) records automatically.
   - Deduplicated via `Beesharp.CallRecorded` in `HttpRequestMessage.Options` so nothing is ever double-counted.

3. **Auto-detected endpoint identity (`EndpointNormalizer` + `EndpointGrouper`):**
   - Groups calls by `METHOD + normalized path`, collapsing numeric IDs, GUIDs, hex tokens, prefixed IDs (`SKU-001`, `cart-412`), dates, and emails to `{id}`, keeping API versions (`v1`/`v2`), stripping query values, and disambiguating hosts only when multiple hosts exist in a run.
   - `EndpointGrouper` adaptively collapses high-cardinality slug segments (> 10 distinct values at the same path position) into `{id}` while keeping static route segments separate. Every individual call still keeps its exact raw URL (`RecordedCall.Url` / `CallSampleDto.Url`).
   - Optional overrides: `.AsCall("...")` on `HttpRequestMessage` or `using (ctx.CallScope("..."))`.

4. **Per-scenario & per-call slow thresholds (`CallThresholds`, `SlowCallRule` in Core):**
   - Precedence: inline `.SlowAbove(TimeSpan)` / `ctx.CallScope(slowAbove: ...)` → scenario `.SlowAbove(...)` / `Scenario.SlowThresholds` (exact, path-only, or `*` wildcard) → automatic `> 2× typical` (unified across both Live `RunManager` and Fake mode, replacing the hardcoded `1500 ms` in Live mode).

## Why
Users often write scenarios with typed HTTP clients (`AddHttpClient<T>()`), SDKs, or `new HttpClient()` directly rather than `ctx.Http` or `MeasureCallAsync`. Focusing `/calls` on normalized endpoints surfaces slow outliers across bees while retaining full drill-down to exact URLs, bees, and logs.

## Consequences
- DuckDB `calls` table bumped to schema v3 (`ALTER TABLE calls ADD COLUMN IF NOT EXISTS endpoint/method/host/url/slow_threshold_ms`).
- Existing `SlowCallRule` / `CallTypicals` moved from `Beesharp.Client/Runs/` to `Beesharp.Core/Services/` so server (`RunManager`) and client share one slowness rule.
