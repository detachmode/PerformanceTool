# 0065. Beesharp fetches the services' traces into the run; the CLI shows traces and hotspots (Jaeger first)

- Status: Accepted (user decisions below); details Proposed
- Date: 2026-11-26

## What
**Accepted (user, 2026-11-26)**
- Beesharp fetches the traces of selected actors from the trace store **in the background** and keeps them with the
  run, so nobody has to open Jaeger or Kibana to see why a call was slow.
- **Hotspots:** per endpoint, where the time of slow calls went (e.g. "80% of slow `GET /products/{id}` calls spent
  most of their time in `shop-api` → `SELECT product`, typically 380 ms").
- **CLI first** (built for agents); the UI and the assistant tools come later.
- **Jaeger first**; Elastic is a later, small step behind the same interface.

**Proposed (Claude's design)**
- **Which traces:** all failed actors (capped), the slowest few calls per endpoint, and a few typical ones per endpoint
  as the baseline for "slow vs normal". Caps per run (e.g. 500 traces) and per trace (spans), plus a request rate limit:
  in production the trace store is shared infrastructure.
- **When:** in batches during the run (spans arrive 5-30 s after an actor ends), a final pass after the run, one retry
  for what is missing. Traces that never arrive are marked "not found" with the likely reason (not arrived yet, service
  not instrumented, sampled out, expired), and the run says "x of y traces found".
- **Jaeger bulk access:** `GET /api/v3/traces?query.service_name=beesharp&query.start_time_min/max=…` filtered on
  `beesharp.run.id` (whole traces in one response, needs Beesharp's own export to the same store) and/or the
  multi-ID `GET /api/traces?traceID=a&traceID=b` (internal API used by Jaeger's UI). Verify both against Jaeger 2.x.
- **Where:** a new trace-span table in the run's DuckDB (and in `--export` bundles), so the CLI, the server, the UI and
  the assistant read local data, offline, after Jaeger's retention.
- **Configuration:** the trace store (kind `jaeger`, URL, token from `${env:…}`) in the Environment, so a dev cluster
  and production differ only there.
- **CLI:**
  - `beesharp runs trace <run> <bee|call> [--max-spans n] [--json]`: one trace (or the part below a call) as a flat,
    bounded span list (depth, service, name, duration, self time, share of the call, error, key attributes such as
    `db.query.text` and HTTP status). Exit code for "trace not found" with the reason.
  - `beesharp runs hotspots <run> [--endpoint ep] [--slow] [--json]`: the hotspot summary per endpoint, with example
    trace IDs to drill into with `runs trace`.
  - Both work on bundles and on a live server (`--url`).
- One aggregation engine for the server, the CLI and later the UI (like `CallAnalytics` in brain 0060).

## Why
Agents (and people in CI) need the answer "why was this slow" without access to the cluster's trace UI. Fetching into
the run makes bundles self-contained and the analysis fast and repeatable. Hotspots summarise instead of dumping
hundreds of spans into an agent's context.

## Consequences
- Needs Beesharp's own spans exported to the same store for the run-ID search (brain 0063); otherwise ID lists only.
- Sampling in production can drop traces; the run reports how complete the picture is.
- The tracing end-to-end stack (brain 0064) gets a Jaeger-backed variant or a fake trace store for its tests.

## Implementation notes (Claude; design choices are Proposed)

**Jaeger 2.23 API, measured against the real thing (`examples/tracing`, query port 16686)**
- `GET /api/v3/traces/{traceId}` works: `{"result":{"resourceSpans":[...]}}`, OTLP JSON, **hex** IDs, nanosecond times as strings, `kind` and `status.code` as numbers, spans grouped by `service.name`, **all services of the trace in one answer**. Optional `start_time` / `end_time` (RFC 3339) narrow the search; `raw_traces=true` switches off the clock-skew adjustment (not used). Missing trace: **HTTP 404** `{"error":{"httpCode":404,"message":"No traces found"}}`; malformed ID: 400. Jaeger adds `@jaeger@warnings` attributes (skipped). The gRPC gateway may stream several JSON values; the reader accepts one or more.
- The multi-ID `GET /api/traces?traceID=a&traceID=b` (Jaeger v1 UI API) **does not exist** in 2.23 (`404 page not found`); `GET /api/traces/{id}` (v1 JSON, one trace) does, and `/api/traces/a,b` is a 400.
- v3 search `GET /api/v3/traces?query.service_name=…&query.start_time_min=…&query.start_time_max=…` needs both times (400 otherwise) and returns **whole traces** (all services) up to `query.search_depth` (default about 20, 1000 works, about 2 MB for 50 traces). `query.attributes` takes a **JSON object** (`{"beesharp.run.id":"run-…"}`) and works; `key=value` is a 400 and the bracket/dotted forms are silently ignored. `query.duration_min` / `duration_max` (`500ms`, `1s`) work. There is no way to filter by a list of trace IDs.
- **Bulk strategy (Proposed): one `GET /api/v3/traces/{id}` per selected trace**, rate limited. It is the only precise way to get exactly the chosen traces, it is the stable v3 API, and ~2 ms per request against a local Jaeger. The run-ID search would return an undirected, capped slice of the run (the newest N traces, 40 KB each) and needs Beesharp's own export; it stays a later optimisation for small runs.

**Modules and types**
- New module `src/Beesharp.TraceStore` (`Beesharp.Services.TraceStore`; tests `tests/Beesharp.TraceStore.Tests`): `ITraceStore` (`GetTraceAsync(id, from, to)` returning Found / NotFound / Unreachable / Error, never throwing), `JaegerTraceStore`, `ITraceStoreFactory`, `TraceStoreSettings` (the Environment keys), `TraceSelector`, `TraceFetchSession`, `TraceFetching` (starts a session from the environment). It references Core, Contracts, Storage only (no OpenTelemetry SDK: that is `Beesharp.Tracing`, which exports); `Beesharp.Engine` references it, `AddBeesharpEngine` registers it. Elastic later = one more `ITraceStore`.
- Wire and engine in Contracts: `TraceSpanDto`, `TraceFetchDto`, `TraceFetchSummaryDto`, `TraceViewDto`, `HotspotsResultDto` and **`TraceAnalytics`** (span tree, self time, the flat bounded list, hotspots), next to `CallAnalytics` (which got `Classify`, the same slow rule as `--slowest`). `IRunStorage` got four plain reads/writes (`SaveTracesAsync`, `GetTraceFetchesAsync`, `GetTraceSpansAsync`, `GetCallFactsAsync`); `GetTraceViewAsync` / `GetHotspotsAsync` are extension methods over them, so both storages and the CLI's bundle readers run the same engine. REST: `GET /api/runs/{id}/traces/{bee|traceId}?call=&maxSpans=` and `/hotspots?endpoint=&slowOnly=&limit=`.
- Storage: DuckDB schema 6 (`trace_fetches`, `trace_spans`, `CREATE TABLE IF NOT EXISTS`, no column changes); `trace_*.parquet` in the parquet bundle, tables in the duckdb export, `traceFetches` / `traceSpans` arrays in the JSON export, all imported back; bundles without them are runs without traces. `InMemoryRunStorage` keeps them too (it has no import).

**Configuration (Proposed key names)** in the run's Environment: `TraceStore.Url` (required), `TraceStore.Kind` (`jaeger`), `TraceStore.Token` (Bearer; `${env:NAME}` supported), tuning `TraceStore.MaxTraces` and `TraceStore.SettleSeconds`. An unsupported kind, a bad URL or an unset `${env:…}` variable is a warning in the run's log and nothing is fetched.

**Selection and fetch policy (Proposed defaults, `TraceFetchOptions`)**: failed bees up to 100 (stopped-run cancellations excluded); per endpoint the 5 slowest slow calls and 3 typical calls (nearest to the median of a seeded 24-call sample, so memory is bounded; typical calls only once an endpoint has 10 calls during the run, all endpoints in the final pass); at most 500 traces and 2000 spans per trace; failed bees keep room of their own in the cap and the rolling "slowest" picks at most twice the final top per endpoint (the final pass adds the true top regardless). First request 15 s after a bee ended, polled every 10 s, batches of 20, at least 200 ms between requests, at most two requests per trace; the final pass waits for the last bee's settle time, fetches, waits the 15 s retry delay and retries once, all within 2 minutes (a stopped run skips the waits). Not-found reasons by heuristic: `not-arrived` (last attempt less than 30 s after the bee ended), `expired` (older than 72 h), else `not-in-store`; `store-unreachable` / `store-error` when the store could not answer. A trace found 15 s after its bee may still miss late spans of a slow collector; it is not refetched (doubt, below).

**Never hurts the run**: `RunManager` only calls `Observe` (bounded, in-memory) from the result callback; requests, waits and writes run on the session's own task; a store that is down is probed once per poll, reported once and again when it answers; a final pass after the run keeps `WaitUntilFinishedAsync` (so `beesharp run`) busy but the run already shows as Finished.

**Visible logs**: the session logs through `ILogger` category `Beesharp.Traces`. `RunManager` gives it a run-bound logger that writes to that run's **host logs** (by run ID, so also after the run finished and while another run is active; `RunManagerLoggerProvider` skips the category to avoid duplicates) and to the console logger when there is one. Information: the store and where, each batch, the last "traces: x of y found, …" line; Warning once per problem and Information when the store recovers; Debug per trace. `beesharp run` prints the line with its summary, in `--json` as `traces`, and `--logs` streams host lines as `[host]`.

**CLI**: `runs trace <target> <bee|traceId> [--call n] [--max-spans n]` (exit code 3, `ExitCodes.TraceNotFound`, when the trace is not stored) and `runs hotspots <target> [--endpoint] [--slow] [--limit]`, on bundles and with `--url`; `runs calls --slowest` and `runs bee` print trace and span IDs. Hotspot rule (Proposed): for each analysed call the spans below the call's span ID are its spans (so it works with or without Beesharp's own spans in the store); a span's self time is its duration minus the union of its children; the same (service, name) is summed per call (N+1 queries); time no span covers is the pseudo span `(outside services)`; the hotspot of a call is the key with the largest sum; baseline = the median of the same key's per-call self time in the endpoint's typical (not slow, not failed) calls with a trace, 0 when absent.

**Doubts**: (1) a trace found early can be incomplete when a collector is slower than `SettleSeconds`; a refetch of traces fetched less than 30 s after their bee ended would fix it at the cost of requests. (2) The rolling selection of "slowest" fetches up to twice the final top per endpoint during the run; the final pass always adds the true top of every endpoint on top of that budget (found in the manual check against the real Jaeger: without it the run's slowest call had been missed), so a run can fetch somewhat more than 5 slowest per endpoint. (3) "typical" is judged by the run's own slow rule (2x typical), so on small runs the baseline is thin. (4) `Tracing:TraceUrlTemplate` (link out, main) and `TraceStore.Url` (fetch) stay separate keys for now.
