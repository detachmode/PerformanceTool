# 0064. The tracing end-to-end test runs on .NET Aspire with its own OTLP receiver

- Status: Accepted (user decision: Aspire, no Jaeger); details Proposed
- Date: 2026-11-26

## What
`tests/Beesharp.Tracing.E2E.Tests` checks brain 0063 end to end with real processes:
- `tests/Beesharp.Tracing.E2E.AppHost` (Aspire) starts Beesharp (`src/Beesharp`) and the example service
  `examples/tracing/shop-api` twice, as `shop-api` and `payment-api`. Beesharp gets the example's "Shop journey" scenario
  and preset, copied into a temp folder.
- The test hosts its own OTLP/HTTP receiver (`OtlpReceiver`, Kestrel + the opentelemetry-proto files compiled with
  `Grpc.Tools`/`Google.Protobuf`). All three processes export to it (`E2E:OtlpEndpoint`), so no Jaeger, no collector
  and no Docker are needed.
- `TracingStack` loads the preset over REST, points it at the `shop-api` Aspire started, runs it for a few seconds and
  waits (bounded) for the spans. `TraceChecks` checks every bee: one `beesharp` root, step spans named after the steps,
  every stored call's span ID exported and the parent of exactly one `shop-api` server span with a database span, the
  Pay call reaching `payment-api`, exactly the three services, no orphan spans. A slow database query is only checked
  if one happened, so random behaviour never decides pass or fail.
- It is part of `Beesharp.slnx`, so it runs in CI's normal `dotnet test` (about 25 s locally).
- CI also validates `examples/tracing/compose.yaml` (both profiles) with `docker compose config`, so the documented
  example does not rot. The compose stack itself was checked by hand (screenshots in
  `docs/screenshots/2026-11-25-tracing-stack/`).

## Why
The user chose Aspire over a Node script against the compose stack: the test is C#, debuggable, runs in `dotnet test`
and gets real addresses and readiness from Aspire. Jaeger is not needed because the test itself is the trace sink; it
sees every span directly instead of querying a backend (Aspire's dashboard is off in tests and has no stable query
API). A compose-based test would need Docker in CI and a much slower, flakier setup.

## Consequences
- The stack is described twice: in Aspire for the test and in `compose.yaml` for people. Changes to the example's
  services or scenario must keep both working.
- Aspire needs its orchestrator (DCP) on the CI runner; it comes with the `Aspire.Hosting` packages.
- Follow-up: the trace fetching and hotspots for the CLI (brain 0065) can reuse this stack for its own end-to-end test.
