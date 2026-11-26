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

For a complete runnable stack with an OpenTelemetry Collector, Jaeger v2 (with persistent Badger storage), optional Elastic APM, and two instrumented .NET demo services (`shop-api` → `payment-api` with simulated slow DB queries and errors), see:

- **[`examples/tracing/README.md`](../examples/tracing/README.md)** — walkthrough, backend comparison (Jaeger vs. Elastic), and `./send-test-trace.sh` verification script.
- **[`examples/tracing/compose.yaml`](../examples/tracing/compose.yaml)** — ready-to-run Docker Compose stack (`docker compose up -d --build`).
- **`tests/Beesharp.Tracing.E2E.Tests`** — automated .NET Aspire end-to-end test that verifies the full `beesharp → shop-api → payment-api` trace tree without Docker (`dotnet test tests/Beesharp.Tracing.E2E.Tests`).
