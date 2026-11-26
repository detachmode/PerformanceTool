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
