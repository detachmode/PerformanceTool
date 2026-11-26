# OpenTelemetry Tracing & Jaeger Setup

Beesharp makes every **actor** (bee) the root of its own distributed trace:

```
actor (root span, service: beesharp)
 └── step (one child span per step visit)
      └── call (HTTP or MeasureCallAsync span, carries traceparent)
           └── your service's server span
                └── database / downstream service spans
```

- **Automatic `traceparent` propagation:** Every outgoing HTTP request from a bee automatically sends a W3C `traceparent` header (with the sampled flag set), whether or not OTLP export is enabled. Your services under test join the bee's trace automatically.
- **Stored in every run:** Each bee's `TraceId` and each call's `SpanId` are stored with the run (DuckDB, REST API, and export bundles) and shown with a copy button in the `/calls` inspector and the bee's **Calls** tab.
- **Opt-in OTLP export:** Exporting Beesharp's own spans (actor, step, call) is **off by default** until you configure an OTLP endpoint. Only Beesharp's actor, step, and call spans are exported — Beesharp's own UI, REST API, and AI assistant traffic are never traced.

---

## 1. Quickstart with Jaeger (Docker Compose)

Jaeger v2 natively accepts OTLP (`4318` for `http/protobuf`, `4317` for `grpc`), so you can connect Beesharp directly to Jaeger without a separate collector.

Create a `compose.yaml`:

```yaml
services:
  jaeger:
    image: jaegertracing/jaeger:2.23.0
    ports:
      - "16686:16686"   # Jaeger UI: http://localhost:16686
      - "4318:4318"     # OTLP http/protobuf (for Beesharp & host services)
      - "4317:4317"     # OTLP gRPC

  beesharp:
    image: ghcr.io/detachmode/beesharp:latest
    ports:
      - "5281:8080"     # Beesharp UI and API: http://localhost:5281
    environment:
      # Send Beesharp's actor, step, and call spans to Jaeger over OTLP http/protobuf
      Tracing__OtlpEndpoint: http://jaeger:4318
    volumes:
      - beesharp-data:/app/data
      - beesharp-presets:/app/presets
      - beesharp-environments:/app/environments
      - beesharp-scripts:/app/scripts
    depends_on:
      - jaeger

volumes:
  beesharp-data:
  beesharp-presets:
  beesharp-environments:
  beesharp-scripts:
```

Start the stack:

```sh
docker compose up -d
```

1. Open Beesharp at **http://localhost:5281** and start a run.
2. Open **`/calls`** (or click a bee and open its **Calls** tab) and copy the **Trace ID** of a slow or failed call.
3. Open Jaeger at **http://localhost:16686/trace/\<traceId\>** (or search Service **`beesharp`** in the Jaeger UI).

You can also filter all traces from a single load run in Jaeger using the tag:

```
beesharp.run.id=<runId>
```

> **Note:** The default `jaegertracing/jaeger:2.23.0` image stores traces in memory. For persistent on-disk storage (Badger) and a shared OpenTelemetry Collector, see the full multi-service example in [`examples/tracing/`](../examples/tracing/README.md).

---

## 2. Running Beesharp Locally, via CLI, or Embedded

If Jaeger is running locally (for example via `docker run --rm -p 16686:16686 -p 4318:4318 -p 4317:4317 jaegertracing/jaeger:2.23.0`), point Beesharp at `http://localhost:4318`.

### Web server (`src/Beesharp`)

```sh
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 dotnet run --project src/Beesharp
```

Or configure it in `appsettings.json`:

```json
{
  "Tracing": {
    "OtlpEndpoint": "http://localhost:4318",
    "Protocol": "http/protobuf",
    "ServiceName": "beesharp"
  }
}
```

### Command-line tool (`beesharp`)

The CLI reads the standard `OTEL_*` environment variables (and `Tracing__*`) and flushes spans before exiting:

```sh
# http/protobuf (default, port 4318)
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 beesharp run --preset smoke

# gRPC (port 4317)
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
beesharp run --preset smoke
```

### Embedded host (`Beesharp.Host`)

`BeesharpApp.CreateBuilder(args)` registers Beesharp tracing automatically using the same `Tracing:*` and `OTEL_*` configuration keys. No extra setup code is required.

---

## 3. Configuration Reference

Export is **off** until an endpoint is set (or when `OTEL_SDK_DISABLED=true`). `Tracing:*` keys take precedence over standard `OTEL_*` environment variables.

| Setting | `appsettings.json` / `Tracing__*` | Standard `OTEL_*` env var | Default |
|---|---|---|---|
| **Endpoint** | `Tracing:OtlpEndpoint` (`Tracing__OtlpEndpoint`) | `OTEL_EXPORTER_OTLP_ENDPOINT` | *(none — export off)* |
| **Traces-only endpoint** | — | `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | *(used as written, without appending `/v1/traces`)* |
| **Protocol** | `Tracing:Protocol` (`Tracing__Protocol`) | `OTEL_EXPORTER_OTLP_PROTOCOL` (or `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL`) | `http/protobuf` (port `4318`); also supports `grpc` (port `4317`) |
| **Service name** | `Tracing:ServiceName` (`Tracing__ServiceName`) | `OTEL_SERVICE_NAME` | `beesharp` |
| **Trace URL template** | `Tracing:TraceUrlTemplate` (`Tracing__TraceUrlTemplate`) | — | *(none — configurable in UI Settings)* |
| **Disable SDK** | — | `OTEL_SDK_DISABLED` | `false` (`true` disables export even if an endpoint is set) |

### Behaviour & safety

- **Base URL handling:** For `http/protobuf`, Beesharp automatically appends `/v1/traces` to `Tracing:OtlpEndpoint` / `OTEL_EXPORTER_OTLP_ENDPOINT` if not already present. `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` is used verbatim.
- **Validation:** An invalid URL or unsupported protocol fails fast at startup (CLI exit code `2`) naming the valid values.
- **Non-blocking export:** Spans use an always-on sampler and a background batch processor (queue size `32,768`, 3-second export timeout). If Jaeger or the collector is slow or unreachable, spans are dropped rather than slowing down the load run.

---

## 4. Connecting Your Services Under Test

Because Beesharp attaches a sampled W3C `traceparent` header to every outgoing HTTP call, any OpenTelemetry-instrumented service automatically attaches its server and downstream spans beneath Beesharp's call span.

Point your services at the same Jaeger or OTel Collector endpoint:

```yaml
environment:
  OTEL_SERVICE_NAME: my-api
  OTEL_EXPORTER_OTLP_ENDPOINT: http://jaeger:4318
  OTEL_EXPORTER_OTLP_PROTOCOL: http/protobuf   # Note: the .NET OTel SDK defaults to gRPC
```

> **Tip for .NET services:** Unlike Beesharp (which defaults to `http/protobuf` on `4318`), the OpenTelemetry .NET SDK defaults to `grpc` (`4317`). Either set `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` on your .NET services or point them at port `4317`.

---

## 5. Span Structure & Attributes

Every actor starts a fresh trace (detached from any ambient ASP.NET Core request activity):

| Span | Kind | Name | Key attributes |
|---|---|---|---|
| **Actor** (root) | `Internal` | Scenario entry label | `beesharp.run.id`, `beesharp.scenario.id`, `beesharp.entry.number`, `beesharp.actor.number`, `beesharp.outcome` (`ok` / `failed` / `canceled`), `beesharp.error` |
| **Step** (child of actor) | `Internal` | Step name (with `#2`, `#3` on repeats) | `beesharp.step.number`, `beesharp.step.name`, `beesharp.step.visit` |
| **Call** (child of step or actor) | `Client` | Normalized endpoint / call name | `beesharp.endpoint`, `http.request.method`, `http.response.status_code`, `server.address`, `server.port`, `url.scheme`, `url.path`, `error.type` |

Failed or canceled actors, failed steps, and failed calls set `StatusCode = Error` with the error message so they highlight immediately in Jaeger.

---

## 6. Full Multi-Service Example

For a complete runnable stack with Jaeger v2, an OpenTelemetry Collector, optional Elastic APM, and two instrumented .NET demo services (`shop-api` → `payment-api` with simulated slow DB queries and errors), see:

### One-click run with .NET Aspire

Requires Docker (for the Jaeger container) and runs `shop-api`, `payment-api`, and `Beesharp` locally as processes with the **Shop journey** preset pre-loaded:

```sh
dotnet run --project examples/tracing/AppHost
```

| Service | URL |
|---|---|
| Aspire Dashboard | http://localhost:18888 |
| Beesharp UI | http://localhost:5281 |
| Jaeger UI | http://localhost:16686 |
| `shop-api` | http://localhost:5290 |

### Docker Compose & Automated Tests

- **[`examples/tracing/README.md`](../examples/tracing/README.md)** — walkthrough, backend comparison (Jaeger vs. Elastic), and `./send-test-trace.sh` verification script.
- **[`examples/tracing/compose.yaml`](../examples/tracing/compose.yaml)** — ready-to-run Docker Compose stack (`docker compose up -d --build`).
- **`tests/Beesharp.Tracing.E2E.Tests`** — automated .NET Aspire end-to-end test that verifies the full `beesharp → shop-api → payment-api` trace tree without Docker (`dotnet test tests/Beesharp.Tracing.E2E.Tests`).

---

## 7. Fetching the services' traces into the run

Beesharp can fetch the distributed traces of selected bees from the trace store **while the run goes on** and keep them
with the run, so nobody has to open Jaeger to see why a call was slow. Jaeger 2.x is supported; Elastic is planned behind
the same interface. The command line (built for CI and AI agents) shows one trace and the hotspots; the UI and the assistant
do not show traces yet (brain 0065).

**Configure** it in the **Environment** the run uses (so a dev cluster and production differ only there):

| Key | Meaning |
|---|---|
| `TraceStore.Url` | Base URL of the trace store's query API, e.g. `http://localhost:16686` (Jaeger). Required; without any `TraceStore.*` key nothing is fetched. |
| `TraceStore.Kind` | `jaeger` (default). |
| `TraceStore.Token` | Optional Bearer token; `${env:NAME}` reads an operating-system variable. |
| `TraceStore.MaxTraces` | Optional cap per run (default 500). |
| `TraceStore.SettleSeconds` | Optional: how long a bee must have ended before its trace is asked for (default 15, spans arrive late). |

This is separate from `Tracing:OtlpEndpoint` (where Beesharp *sends* its own spans) and from `Tracing:TraceUrlTemplate`
(the link to open a trace in your tracing tool).

**What is fetched:** all failed bees (up to 100), the 5 slowest slow calls and 3 typical calls (the baseline) per endpoint,
at most 500 traces and 2000 spans per trace, in batches of 20 with at least 200 ms between requests and at most two
requests per trace. After the run, the last traces are fetched and the missing ones retried once. The traces are saved with
the run (also in `--export` bundles), so they stay available after the trace store has forgotten them. A trace store that is
down never fails or slows the run.

**Where to see it:**

```sh
beesharp runs hotspots latest --url http://localhost:5281      # per endpoint: which span took the time of the slow calls
beesharp runs trace latest <traceId|B5> --url http://localhost:5281   # one bee's trace as a flat list (self time, share, db.query.text)
beesharp runs logs latest --entry host --url http://localhost:5281    # what the fetcher did (category Beesharp.Traces)
```

`beesharp run` prints "Traces  42 of 50 found" with its summary, includes `traces` in `--json`, and shows the fetcher's lines
with `--logs info`. The fetcher logs the store and where, each batch, a final "traces: 42 of 50 found, 8 not found: …" line,
one warning per problem (store unreachable or answering with an error), and per-trace details at Debug.

**Traces that are missing** are recorded with a likely reason: `not-arrived` (the bee ended shortly before the last try),
`not-in-store` (the store does not have it: service not instrumented, sampled out, or exporting elsewhere), `expired`
(older than 72 h), `store-unreachable`, `store-error`; for traces never asked for, `not-selected` and `not-configured`.
`runs trace` prints the reason and exits with code 3.

See [`docs/cli.md`](cli.md) for all options and [`examples/tracing/README.md`](../examples/tracing/README.md) to try it with the compose stack.
