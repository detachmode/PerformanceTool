# Trace backends for a dev cluster: Jaeger vs Elastic

Two dev setups side by side, to compare them before Beesharp fetches traces by trace ID. Dev only: Elastic
security is off and nothing is tuned for production.

```sh
docker compose --profile jaeger up -d                     # Jaeger only
docker compose --profile elastic up -d                    # Elastic only
docker compose --profile jaeger --profile elastic up -d   # both
./send-test-trace.sh                                      # needs curl, jq, openssl
docker compose --profile jaeger --profile elastic down    # add -v to delete the stored traces
```

`send-test-trace.sh` sends one trace (`beesharp` → `shop-api`) to every running backend and reads it back by
trace ID, the same lookup Beesharp would do for a slow call:

```
trace id: 4d7ce6ac47fbe9b3822d67a473ce9774
jaeger:  beesharp, shop-api
         http://localhost:16686/trace/4d7ce6ac47fbe9b3822d67a473ce9774
elastic: beesharp (transaction), shop-api (transaction)
         http://localhost:5601/app/apm/link-to/trace/4d7ce6ac47fbe9b3822d67a473ce9774
```

## Endpoints

| | Jaeger | Elastic |
|---|---|---|
| Services send OTLP to | `:4317` gRPC, `:4318` HTTP | `:14317` gRPC, `:14318` HTTP (OTel Collector → APM Server) |
| UI | http://localhost:16686 | http://localhost:5601 (Observability → Applications) |
| Trace by ID | `GET :16686/api/v3/traces/{traceId}` → OTLP JSON | `POST :9200/traces-apm*/_search`, term `trace.id` → Elastic's own field layout |

## Comparison (measured idle on a Mac, Docker Desktop)

| | Jaeger | Elastic |
|---|---|---|
| Containers | 1 (`jaeger`) | 4 (`elasticsearch`, `kibana`, `apm-server`, `otel-collector`) |
| Memory | ~20 MB | ~3 GB (Elasticsearch ~1.5 GB, Kibana ~1.5 GB) |
| Images | ~170 MB | ~4.5 GB |
| Storage | Badger, embedded, on the `jaeger-badger` volume, 72 h TTL | Elasticsearch on the `elastic-data` volume |
| Scaling | one replica only (Badger is single-process) | can grow to a real cluster |
| Same as production? | no, a second adapter in Beesharp | yes, if production is Elastic with APM Server |

## Notes

- **APM Server takes OTLP as protobuf only.** Sending OTLP JSON straight to `:8200` fails with
  `400 failed to unmarshal request body`. Real SDKs send protobuf, but the OTel Collector in front accepts both
  and is also where tail sampling would go in a cluster.
- **Field layout:** APM Server converts OTLP to Elastic's own fields (`trace.id`, `parent.id`, `transaction.*` /
  `span.*`, durations in µs). Ingesting through Elastic's own OTel collector (EDOT) instead keeps OTel field
  names; the Beesharp adapter must match what production uses.
- **Disk check off:** `cluster.routing.allocation.disk.threshold_enabled: false`, because Elasticsearch refuses new
  data when the Docker disk is over 90% full. Remove it anywhere that is not a laptop.
- APM Server logs `refresh cache elasticsearch returned status 404` until Kibana has set up APM agent
  configuration. It is harmless here.
