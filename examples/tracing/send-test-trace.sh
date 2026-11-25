#!/usr/bin/env bash
# Sends one two-span trace (beesharp → shop-api) over OTLP/HTTP to the collector (:4318),
# then reads it back by trace ID — the same lookup Beesharp would do for a slow call.
set -euo pipefail

trace_id=$(openssl rand -hex 16)
client_span=$(openssl rand -hex 8)
server_span=$(openssl rand -hex 8)
now=$(( $(date +%s) * 1000000000 ))
ms=1000000

payload=$(cat <<JSON
{"resourceSpans":[
 {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"beesharp"}}]},
  "scopeSpans":[{"scope":{"name":"Beesharp"},"spans":[
   {"traceId":"$trace_id","spanId":"$client_span","name":"GET /api/products/{id}","kind":3,
    "startTimeUnixNano":"$now","endTimeUnixNano":"$(( now + 480 * ms ))",
    "attributes":[{"key":"http.request.method","value":{"stringValue":"GET"}},
                  {"key":"beesharp.step","value":{"stringValue":"Open product"}}]}]}]},
 {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"shop-api"}}]},
  "scopeSpans":[{"scope":{"name":"Microsoft.AspNetCore"},"spans":[
   {"traceId":"$trace_id","spanId":"$server_span","parentSpanId":"$client_span","name":"GET /api/products/{id}","kind":2,
    "startTimeUnixNano":"$(( now + 30 * ms ))","endTimeUnixNano":"$(( now + 450 * ms ))",
    "attributes":[{"key":"http.response.status_code","value":{"intValue":200}}]}]}]}
]}
JSON
)

echo "trace id: $trace_id"

post() { curl -fsS -o /dev/null -X POST -H 'Content-Type: application/json' -d "$payload" "$1/v1/traces"; }

# Retries a command for up to 30 seconds (backends store spans in batches).
retry() { for _ in $(seq 30); do out=$("$@" 2>/dev/null) && [ -n "$out" ] && { echo "$out"; return 0; }; sleep 1; done; return 1; }

jaeger_spans() {
  curl -fsS "http://localhost:16686/api/v3/traces/$trace_id" \
    | jq -er '[.result.resourceSpans[] | .resource.attributes[] | select(.key=="service.name") | .value.stringValue] | select(length==2) | join(", ")'
}

elastic_spans() {
  curl -fsS -H 'Content-Type: application/json' "http://localhost:9200/traces-apm*/_search" \
    -d "{\"query\":{\"term\":{\"trace.id\":\"$trace_id\"}},\"sort\":[{\"@timestamp\":\"asc\"}]}" \
    | jq -er '[.hits.hits[]._source | "\(.service.name) (\(.processor.event))"] | select(length==2) | join(", ")'
}

# One send to the collector, which forwards to whichever backends .env names.
post http://localhost:4318

if curl -fs -o /dev/null http://localhost:16686; then
  echo "jaeger:  $(retry jaeger_spans || echo 'not found')"
  echo "         http://localhost:16686/trace/$trace_id"
fi

if curl -fs -o /dev/null http://localhost:9200; then
  echo "elastic: $(retry elastic_spans || echo 'not found')"
  echo "         http://localhost:5601/app/apm/link-to/trace/$trace_id"
fi
