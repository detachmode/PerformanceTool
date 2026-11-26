# Tracing example: Beesharp as the root of every trace (brain 0063)

A dev cluster in miniature. Beesharp loads two traced services; Beesharp and the services all send their spans to
**one OpenTelemetry collector** (OTLP http/protobuf on port 4318), and the collector forwards them to Jaeger and/or
Elastic. Every actor (bee) is one trace: actor → steps → calls → the services' own spans.

```
 beesharp ──calls with traceparent──▶ shop-api ──▶ payment-api
     │                                   │              │
     └───────────── spans, OTLP :4318 ───┴──────────────┘
                          ▼
                   otel-collector ──▶ Jaeger and/or Elastic (APM Server)
```

Dev only: Elastic security is off and nothing is tuned for production.

## Run

```sh
docker compose up -d --build      # backend chosen in .env (Jaeger by default)
```

1. Open Beesharp at http://localhost:5281, load the preset **Shop journey** and press play.
2. On `/calls`, pick a slow call and copy its trace ID.
3. Open it in Jaeger: `http://localhost:16686/trace/<traceId>`, or in Kibana:
   `http://localhost:5601/app/apm/link-to/trace/<traceId>`.

`./send-test-trace.sh` (needs curl, jq and openssl) checks the pipeline without Beesharp: it sends one trace to the
collector and reads it back by trace ID from every running backend.

`docker compose down` stops everything; add `-v` to also delete the stored runs and traces.

## See why a call was slow without opening Jaeger (brain 0065)

Beesharp can fetch the traces of selected bees back from Jaeger **during the run** and keep them with it. Name the trace
store in the **environment** the run uses (Environments dialog, or a file `environments/Local.json`):

```json
{ "name": "Local", "variables": [
  { "key": "TraceStore.Url", "value": "http://jaeger:16686" } ] }
```

`http://localhost:16686` when Beesharp runs on the host, `http://jaeger:16686` inside the compose network. `TraceStore.Kind`
(`jaeger`, the default), `TraceStore.Token` (a Bearer token, or `${env:NAME}`), `TraceStore.MaxTraces` and
`TraceStore.SettleSeconds` are optional (see `docs/cli.md`). Select that environment, run the **Shop journey** preset, then
ask the CLI (it works on the server with `--url`, or offline on an exported bundle):

```sh
beesharp runs hotspots latest --url http://localhost:5281                 # per endpoint: which span took the time of the slow calls
beesharp runs trace latest <traceId or B5> --url http://localhost:5281    # one bee's trace as a flat list with self time and share
beesharp runs logs latest --entry host --url http://localhost:5281        # what the background fetch did ("traces: 42 of 50 found")
```

Trace IDs come from `runs hotspots`, `runs calls --slowest` and `runs bee`. The first traces are fetched about 15 s after
their bee ended (spans arrive late) and the last ones when the run is over. `beesharp run` does the same from the command
line (`beesharp run --preset "Shop journey" --env Local --logs info`) and prints "Traces  42 of 50 found" with its summary.

## Automated test

`tests/Beesharp.Tracing.E2E.Tests` (brain 0064) runs the same setup without Docker: Aspire starts Beesharp,
`shop-api` and `payment-api` as processes, the test receives their spans itself and checks every bee's trace tree
(root → steps → calls → shop-api → database / payment-api); its receiver also answers Jaeger's trace query, so the run fetches its traces from it and `runs trace` / `runs hotspots` are checked end to end. It is part of `dotnet test`:

```sh
dotnet test tests/Beesharp.Tracing.E2E.Tests
```

## Pick the backend (`.env`)

| Backend | `COMPOSE_PROFILES` | `TRACE_EXPORTERS` |
|---|---|---|
| Jaeger (default) | `jaeger` | `[otlp_http/jaeger]` |
| Elastic | `elastic` | `[otlp_http/elastic]` |
| Both, to compare | `jaeger,elastic` | `[otlp_http/jaeger, otlp_http/elastic]` |

The profiles start the backends and `TRACE_EXPORTERS` tells the collector where to forward, so the two must match.

## What runs

| Service | Port | What it is |
|---|---|---|
| `otel-collector` | 4318 | The cluster's one OTLP endpoint (http/protobuf), also for services running on the host. |
| `beesharp` | 5281 | Built from this repo. `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318`; mounts `beesharp/shop-journey.cs` (scenario) and `beesharp/shop-journey.json` (preset). |
| `shop-api` | 5290 | Demo service under test (`shop-api/`), plain OpenTelemetry .NET. Its fake database queries are spans of their own and sometimes slow, so a trace shows *why* a call was slow. `/pay` calls `payment-api`. |
| `payment-api` | — | The same image under another service name; fails now and then (503). |
| `jaeger` (profile `jaeger`) | 16686 | Jaeger v2 with Badger on the `jaeger-badger` volume (72 h TTL). Query: `GET /api/v3/traces/{traceId}` returns OTLP JSON. |
| `elasticsearch`, `kibana`, `apm-server` (profile `elastic`) | 9200, 5601 | Query: `POST :9200/traces-apm*/_search`, term `trace.id`; Elastic's own field layout. |

## Beesharp's tracing settings

Off unless an endpoint is set. `Tracing:*` wins over the standard `OTEL_*` variables:

| Setting | Environment variable | Default |
|---|---|---|
| Endpoint | `Tracing__OtlpEndpoint` or `OTEL_EXPORTER_OTLP_ENDPOINT` | none (export off) |
| Protocol | `Tracing__Protocol` or `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` (port 4318); `grpc` for 4317 |
| Service name | `Tracing__ServiceName` or `OTEL_SERVICE_NAME` | `beesharp` |

Beesharp running on the host (not in compose) sends to the same collector:

```sh
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 dotnet run --project src/Beesharp
```

## Comparison of the backends (measured idle on a Mac, Docker Desktop)

| | Jaeger | Elastic |
|---|---|---|
| Containers | 1 | 3 (`elasticsearch`, `kibana`, `apm-server`) |
| Memory | ~20 MB | ~3 GB (Elasticsearch ~1.5 GB, Kibana ~1.5 GB) |
| Images | ~170 MB | ~4.3 GB |
| Storage | Badger, embedded, one replica only | Elasticsearch, can grow to a real cluster |

## Notes

- **The .NET SDK defaults to gRPC.** Services built with OpenTelemetry .NET need
  `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` to use port 4318 (see `shop-api` in `compose.yaml`).
- **APM Server takes OTLP as protobuf only**: OTLP JSON sent straight to `:8200` fails with `400 failed to unmarshal
  request body`. The collector always forwards protobuf.
- **Field layout:** APM Server converts OTLP to Elastic's own fields (`trace.id`, `parent.id`, `transaction.*` /
  `span.*`, durations in µs). Ingesting through Elastic's own OTel collector (EDOT) instead keeps OTel field names.
- **Disk check off:** `cluster.routing.allocation.disk.threshold_enabled: false`, because Elasticsearch refuses new
  data when the Docker disk is over 90% full. Remove it anywhere that is not a laptop.
- APM Server logs `refresh cache elasticsearch returned status 404` until Kibana has set up APM agent
  configuration. It is harmless here.
