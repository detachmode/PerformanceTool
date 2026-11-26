# The command-line tool (`beesharp`)

`src/Beesharp.Cli` runs presets **without the web server and the UI** (CI pipelines, brain 0054) and inspects runs
**offline** from exported bundles or **live** from a running server (brain 0061). It is built to be called by people,
CI jobs and AI agents alike: plain text without colour codes by default, untruncated `--json` everywhere.

This page is the reference for every command and option. `beesharp <command> --help` prints the same information;
`CliDocumentationTests` fails when a command or option is missing here.

- [Install or run](#install-or-run)
- [Commands at a glance](#commands-at-a-glance)
- [Conventions](#conventions)
- [`beesharp run`](#beesharp-run--run-a-preset-headlessly)
- [`beesharp runs`](#beesharp-runs--inspect-runs)
  [`list`](#beesharp-runs-list) · [`summary`](#beesharp-runs-summary) · [`errors`](#beesharp-runs-errors) ·
  [`calls`](#beesharp-runs-calls) · [`logs`](#beesharp-runs-logs) · [`bee`](#beesharp-runs-bee) ·
  [`compare`](#beesharp-runs-compare) · [`timeline`](#beesharp-runs-timeline) · [`hotspots`](#beesharp-runs-hotspots) · [`trace`](#beesharp-runs-trace)
- [Environment variables](#environment-variables)
- [Exit codes](#exit-codes)
- [The embedded host's `run` command](#the-embedded-hosts-run-command)
- [GitHub Actions example](#github-actions-example)

## Install or run

Install `Beesharp.Cli` from NuGet as a global .NET tool (requires the .NET 11 SDK/runtime; the command is `beesharp`):

```bash
dotnet tool install --global Beesharp.Cli
dotnet tool update --global Beesharp.Cli     # update an existing installation
```

From a local checkout:

```bash
dotnet pack src/Beesharp.Cli -c Release -o ./nupkg
dotnet tool install --global Beesharp.Cli --add-source ./nupkg
```

Or run from source without installing (everything after `--` goes to `beesharp`):

```bash
dotnet run --project src/Beesharp.Cli -- run --preset "Black Friday" --env Staging --root ./load-tests
```

## Commands at a glance

```bash
beesharp --help
beesharp run --preset <name|file> [--env <name>] [--root <folder>] [options]

beesharp runs list     [--url <url>] [--limit <n>] [--json]
beesharp runs summary  <target> [--url <url>] [--json]
beesharp runs errors   <target> [--url <url>] [--json]
beesharp runs calls    <target> [--url <url>] [--entry <n>] [--scenario <id>] [--step <n>] [--by-step]
                                [--slowest <n>] [--endpoint <ep>] [--slow] [--failed] [--min-ms <ms>] [--search <q>] [--json]
beesharp runs logs     <target> [--url <url>] [--bee <n|Bn>] [--entry <n|preflight|shutdown|host>] [--step <n>]
                                [--level <lvl[,lvl...]>] [--search <q>] [--offset <n>] [--limit <n>] [--json]
beesharp runs bee      <target> <bee> [--url <url>] [--json]
beesharp runs compare  <targetA> <targetB> [--url <url>] [--json]
beesharp runs timeline <target> [--url <url>] [--bucket <seconds>] [--entry <n>] [--scenario <id>] [--json]
beesharp runs hotspots <target> [--url <url>] [--endpoint <ep>] [--slow] [--limit <n>] [--json]
beesharp runs trace    <target> <bee|traceId> [--url <url>] [--call <n>] [--max-spans <n>] [--json]
```

| Command | What it answers |
|---|---|
| `run` | Runs a preset headlessly, prints progress and a summary, saves and optionally exports the run. |
| `runs list` | Which runs does a server have? |
| `runs summary` | How did a run go overall, per scenario entry and per step? |
| `runs errors` | Which errors happened, how often, where, and in which bees? |
| `runs calls` | Which endpoints are slow or failing, and which single calls are the slowest? |
| `runs logs` | What did the bees, the preflight, the shutdown program or the host log? |
| `runs bee` | Everything about one bee: steps, calls, errors, logs. |
| `runs compare` | Did the candidate run get slower or fail more than the baseline? |
| `runs timeline` | How did load, latency and failures develop over the run? |
| `runs hotspots` | Per endpoint: where did the time of the slow calls go (which service and span)? Needs fetched traces. |
| `runs trace` | One bee's distributed trace as a flat span list: which span took the time? Needs fetched traces. |

Every command accepts `-h`, `--help` or `-?`.

## Conventions

- **Targets.** `<target>` is either an exported run bundle — `.zip` (Parquet), `.duckdb` or `.json`, read offline — or,
  with `--url <url>`, a run on a live server: an exact run ID, `latest` (most recent run) or `prev` (the one before).
  `BEESHARP_URL` stands in for `--url`, but only when the target is not a local file.
- **Offline reading never touches a live database.** `.zip` bundles are extracted lazily (only the Parquet tables a
  command needs, e.g. `summary`, `errors`, `calls` and `compare` never extract `logs.parquet`) and queried with DuckDB
  `read_parquet(...)`; `.duckdb` files are opened read-only.
- **Numbering is 1-based**, as in the UI: scenario entry `1`, step `1`, bee `1` (also written `B1`).
- **`--json`** prints untruncated camelCase JSON to stdout and nothing else, so agents can parse it. On `runs`
  commands it is a plain flag; on `run` it takes an optional file (see below).
- **Trace IDs.** Runs store the W3C trace and span ID of every call (brain 0063) and exported bundles contain them.
  `runs calls --slowest` prints the trace ID under each call, `runs bee` prints the bee's trace ID and each call's span
  ID (both also in `--json` as `traceId` / `spanId`). Give a trace ID to `runs trace` to see what the services did.

---

## `beesharp run` — run a preset headlessly

```bash
beesharp run --preset <name|file> [--env <name>] [--root <folder>] [options]
```

The run starts with the preflight, then the scenario entries, then the shutdown program, as in the UI. Ctrl+C stops the
run like Stop in the UI: bees in flight become canceled, the shutdown program still runs, the run is saved.

| Option | Meaning |
|---|---|
| `-p, --preset <name\|file>` | The preset's name (from `<root>/presets/`) or the path of a preset `.json` file. Required. |
| `-e, --env <name>` | The environment (`<root>/environments/`) whose `{{Key}}` values are filled in. Needed when the preset uses placeholders; unknown keys refuse to start. |
| `--root <folder>` | Holds `presets/`, `environments/`, `scripts/` (with `preflight/`, `shutdown/`) and `data/`. Default: the current directory. |
| `--storage <kind>` | `duckdb` (default) saves the run to `<root>/data/beesharp.duckdb`; start the server on the same root and the run shows in the UI's history. `memory` saves nothing. |
| `-d, --duration <seconds>` | Run for this long instead of the preset's duration. |
| `--progress <seconds>` | Print a progress line this often (default 5, `0` = none). |
| `--logs [level]` | Stream live scenario logs (`trace\|debug\|info\|warn\|error`; default `info`). |
| `--json [file]` | Print the summary as JSON to stdout (suppresses the other output), or with a file name also write the JSON to `<file>`. |
| `--export <path>` | Export the finished run to a file or directory (e.g. `run.zip` or `exports/`). |
| `--export-format <fmt>` | `parquet`, `duckdb`, `json` or `csv` (inferred from `--export` when omitted). |
| `-h, --help` | Print the help text. |

```bash
beesharp run --preset "Black Friday" --env Staging --root ./load-tests
beesharp run --preset smoke --duration 30 --progress 0 --json summary.json --export run.zip
beesharp run --preset ./presets/checkout.json --logs warn --storage memory
```

### Sending traces to a collector (brain 0063)

Every bee is the root of its own trace and every call carries a sampled `traceparent`, whatever you configure; the
trace IDs are stored with the run. To also **export** the bees' spans (bee → step → call) over OTLP, set the standard
OpenTelemetry environment variables; without an endpoint nothing is exported:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 beesharp run --preset smoke        # http/protobuf (default)
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 OTEL_EXPORTER_OTLP_PROTOCOL=grpc beesharp run --preset smoke
```

`OTEL_SERVICE_NAME` (default `beesharp`) names the service. A bad endpoint or protocol exits with code 2. The spans
are flushed when the run ends; a collector that is down costs at most 3 seconds there and never fails the run.
`examples/tracing/` has a ready-made collector with Jaeger and Elastic.

To also **fetch** the services' traces back into the run (for `runs trace` and `runs hotspots`), name the trace store in the
run's environment (`TraceStore.Url`, see [Fetching the services' traces into the run](#fetching-the-services-traces-into-the-run)).
The run then waits for the final fetch after the last bee, the summary gets a `Traces` line, `--json` gets a `traces` object, and
`--logs` also streams the fetcher's lines (marked `[host]`).

---

## `beesharp runs` — inspect runs

```bash
beesharp runs <subcommand> <target> [options]
```

All subcommands take `--url <url>` (query a running server; `BEESHARP_URL` is used when the target is not a local
file), `--json` and `-h, --help` (also `-?`). Only the options that differ are listed per subcommand.

### `beesharp runs list`

Lists the stored runs of a running server (needs `--url` or `BEESHARP_URL`).

| Option | Meaning |
|---|---|
| `--url <url>` | Beesharp server URL (default: `BEESHARP_URL`). |
| `--limit <n>` | Maximum runs to fetch (1 to 500, default 20). |
| `--json` | Print untruncated JSON. |

```bash
beesharp runs list --url http://localhost:5281 --limit 5
```

### `beesharp runs summary`

Run metadata (`runId`, `state`, `startedUtc`, `elapsedSeconds`, `error`), total/ok/failed/canceled/slow bees,
per-scenario and per-step counts with call latency (`min`/`p50`/`p95`/`max`/`avg`) and slow counts, and the overall
`min`/`p50`/`p95`/`max` bee durations.

```bash
beesharp runs summary run.zip
beesharp runs summary latest --url http://localhost:5281 --json
```

### `beesharp runs errors`

Grouped, untruncated error texts with counts, the scenario entries and steps they happened in, and sample bee numbers.

```bash
beesharp runs errors run.zip --json
```

### `beesharp runs calls`

Endpoint latency breakdown (endpoint normalization and grouping of brain 0060), slow thresholds, and slow/failing call
counts. With `--slowest <n>` it lists the slowest single calls instead, with bee, step, timing split and captured
headers.

| Option | Meaning |
|---|---|
| `--entry <n>` | Only scenario entry `<n>` (1-based). |
| `--scenario <id>` | Only entries whose scenario ID or label matches. |
| `--step <n>` | Only step `<n>` (1-based). |
| `--by-step` | Split endpoints by step (useful when several steps call the same URL). |
| `--slowest <n>` | List the `<n>` slowest single calls (1 to 1000). |
| `--endpoint <ep>` | Only endpoints containing `<ep>` (with the summary or `--slowest`). |
| `--slow` | Only endpoints/calls marked slow. |
| `--failed` | Only endpoints/calls with failures. |
| `--min-ms <ms>` | Only calls of at least `<ms>` milliseconds. |
| `--search <q>` | Only endpoints/calls whose normalized name, URL, host or step name contains `<q>`. |

```bash
beesharp runs calls run.zip --slow --min-ms 250
beesharp runs calls run.zip --slowest 20 --endpoint /products --json
beesharp runs calls latest --url http://localhost:5281 --by-step --entry 1
```

### `beesharp runs logs`

Filtered, paged log lines of the bees, the preflight, the shutdown program and the host.

| Option | Meaning |
|---|---|
| `--bee <n\|Bn>` | Only bee `<n>` (1 or B1 = first bee). |
| `--entry <n\|preflight\|shutdown\|host>` | Only scenario entry `<n>` (1-based), or the preflight, the shutdown program or the host. |
| `--step <n>` | Only step `<n>` (1-based). |
| `--level <lvl[,lvl...]>` | Only these levels: `trace`, `debug`, `info`, `warn`, `error` (comma-separated or repeated). |
| `--search <q>` | Case-insensitive text in the message, exception or bee name. |
| `--offset <n>` | Zero-based row offset (default 0). |
| `--limit <n>` | Maximum lines (default 200, max 10000). |

```bash
beesharp runs logs run.zip --entry 1 --step 2 --level warn,error --limit 50
beesharp runs logs run.zip --entry preflight
```

### `beesharp runs bee`

```bash
beesharp runs bee <target> <bee> [--url <url>] [--json]
```

Everything about one bee (`<bee>` = 1-based number, `1` or `B1`): step visits, HTTP calls (labels, optional TTFB/read
timing split, captured headers), status codes, errors and logs.

```bash
beesharp runs bee run.zip B4 --json
```

### `beesharp runs compare`

```bash
beesharp runs compare <targetA> <targetB> [--url <url>] [--json]
```

Compares a baseline `<targetA>` with a candidate `<targetB>`: overall and per-scenario latency (`p50`/`p95`/`max`),
error rate, failed and slow bees. With `--url`, `prev latest` compares the last two runs.

```bash
beesharp runs compare baseline.zip candidate.zip
beesharp runs compare prev latest --url http://localhost:5281 --json
```

### `beesharp runs timeline`

Time buckets over the run: bees in flight, requests per second, `p50`/`p95`/`max` latency, slow and failed counts.

| Option | Meaning |
|---|---|
| `--bucket <seconds>` | Bucket width in seconds (default 5). |
| `--entry <n>` | Only scenario entry `<n>` (1-based). |
| `--scenario <id>` | Only entries whose scenario ID or label matches. |

```bash
beesharp runs timeline run.zip --bucket 10
```

### `beesharp runs hotspots`

Where the time of slow calls went, per endpoint (brain 0065). Works on the distributed traces that were fetched into the
run ([see below](#fetching-the-services-traces-into-the-run)). For each endpoint it takes the slow calls whose trace
was fetched and finds, per call, the span (service + name) with the largest **self time** (its own time without its
children, summed over the call, so five 100 ms queries count as 500 ms). It prints how many slow calls each span
explains, its typical self time and share of the call, example trace IDs for `runs trace`, and the same span's typical
time in the endpoint's typical calls (the baseline) with the factor. Time that no service span covers (network,
queueing, client side) shows as the pseudo span `(outside services)`. An endpoint with no slow call that has a trace
shows where its typical calls spend their time instead. The summary line says "x of y traces found"; the endpoints
with the most slow traced calls come first.

| Option | Meaning |
|---|---|
| `--endpoint <ep>` | Only endpoints containing `<ep>` (case-insensitive). |
| `--slow` | Only endpoints that have slow calls with a fetched trace. |
| `--limit <n>` | Show at most `<n>` endpoints (1 to 100, default 10); each shows its top 5 hotspots. |

```bash
beesharp runs hotspots latest --url http://localhost:5281
beesharp runs hotspots run.zip --slow --endpoint /products --json
```

```
Hotspots in run run-20261126-012951-987: where the time of slow calls went
  traces: 51 of 51 found
GET /products/{id}: 56 of 540 calls slow (typical 40 ms, slow above 80 ms)
  22 slow call(s) with a trace (median 718 ms), 131 typical call(s) as the baseline
  1. shop-api  SELECT product
     explains 22 of 22 slow calls (100%); typically 716 ms = 100% of the call, 19.4x the baseline of 37 ms
     db.query.text=SELECT product  db.system.name=postgresql
     traces: 14c1e1a5...  (beesharp runs trace <run> <id>)
```

### `beesharp runs trace`

```bash
beesharp runs trace <target> <bee|traceId> [options]
```

One bee's distributed trace as a flat, bounded span list: depth, service, span name, duration, **self time**, its
**share** of the bee's duration (of the call's duration with `--call`), error flag, HTTP status and key attributes such
as `db.query.text`. `<bee>` is a 1-based bee (`5` or `B5`); `<traceId>` is the 32-hex trace ID printed by
`runs calls --slowest`, `runs bee` or `runs hotspots`. The summary (spans, services, errors, why the trace was fetched)
comes first. Over `--max-spans` the spans with the largest self time are kept together with their ancestors, in tree
order, and a note says how many were left out. With `--json` the list is the same, with all stored attributes and no
shortening of text.

If the trace is **not stored with the run** the command prints why and exits with code **3**: not selected for
fetching, not found in the trace store (with the likely reason: not arrived yet, not in the store, expired), the trace
store could not be reached, or no trace store was configured for the run.

| Option | Meaning |
|---|---|
| `--call <n>` | Only the spans below call `<n>` of the bee (1-based, as numbered by `runs bee`); shares are of the call's duration. |
| `--max-spans <n>` | Show at most `<n>` spans (1 to 5000, default 50). |

```bash
beesharp runs trace latest B8 --url http://localhost:5281
beesharp runs trace run.zip 4d7ce6ac47fbe9b3822d67a473ce9774 --call 3 --json
```

```
Trace B96  14c1e1a552ed247eb3aa71987403a376   (run run-20261126-012951-987)
  Spans       27 (showing 14) in 3 service(s): beesharp, payment-api, shop-api
  Reference   the bee's duration 1.17 s: shares are of that
  Span                                        Service       Duration       Self   Share  Status
  Shop journey                                beesharp        1.17 s       0 ms      0%
    Open product                              beesharp        876 ms       0 ms      0%
      GET /products/{id}                      beesharp        876 ms       2 ms     <1%  HTTP 200
        GET /products/{id:int}                shop-api        874 ms       0 ms      0%  HTTP 200
          SELECT product                      shop-api        874 ms     874 ms     75%
            db.query.text=SELECT product  db.system.name=postgresql
```

### Fetching the services' traces into the run

Both commands read traces that Beesharp fetched **in the background while the run went on** (brain 0065), from the
trace store named in the run's **environment** (brain 0033). Nothing is fetched, and nothing about it is logged beyond one
line, when the environment has no `TraceStore.*` key.

| Environment key | Meaning |
|---|---|
| `TraceStore.Url` | Base URL of the trace store's query API, e.g. `http://localhost:16686` for Jaeger 2.x. Required. |
| `TraceStore.Kind` | `jaeger` (the default, and the only kind so far; Elastic is planned). |
| `TraceStore.Token` | Optional Bearer token. `${env:NAME}` reads an operating-system variable, so no secret is stored in the file. |
| `TraceStore.MaxTraces` | Optional cap on the traces fetched for one run (default 500). |
| `TraceStore.SettleSeconds` | Optional: how long a bee must have ended before its trace is asked for (default 15; spans arrive 5 to 30 s late). |

What is fetched: all failed bees (up to 100), the 5 slowest slow calls and 3 typical calls (as the baseline) per
endpoint, in batches of 20 with at least 200 ms between two requests (the trace store is shared infrastructure), at most
two requests per trace (one retry), at most 2000 spans per trace. After the run (`beesharp run` waits for it, at most
two minutes; a stopped run does not wait) the last traces are fetched, the missing ones retried once, and each trace that
never came is recorded with its likely reason. The traces are saved with the run (DuckDB, and in the `--export` bundles as
`trace_spans.parquet` / `trace_fetches.parquet`), so the commands work offline, long after the trace store forgot them.
A trace store that is down never fails or slows the run.

**What the fetcher says** goes to the run's host logs, category `Beesharp.Traces`: Information for the store and where, each batch
("fetched 18 of 20 traces (2 not arrived yet, retry in 15 s)") and the last line ("traces: 42 of 50 found, 8 not found: 6 not
arrived, 2 not in the store (sampled out or not instrumented)"); a Warning once per problem (store unreachable or answering
with an error) and an Information line when it answers again; Debug for each trace (ID, bee, why it was chosen, span count).
See them with `beesharp runs logs <target> --entry host` (add `--level debug` for the per-trace lines), live with
`beesharp run --logs info` (lines marked `[host]`), and in the UI's Logs page. `beesharp run` also prints the last line
with its summary (`Traces  42 of 50 found`) and puts it in `--json` as `traces` (`selected`, `found`, `notFound`, `errors`,
`reasons`, `line`; `null` when no trace store was configured).

The not-found reasons: `not-arrived` (the bee ended shortly before the last attempt), `not-in-store` (the store answers but
does not have it: services not instrumented, sampled out, or exporting elsewhere), `expired` (older than 72 hours),
`store-unreachable`, `store-error`; for a trace that was never asked for: `not-selected` (not one of the chosen ones) and
`not-configured` (no trace store configured, or the run is from before this feature).

---

## Environment variables

| Variable | Used by | Meaning |
|---|---|---|
| `BEESHARP_URL` | `runs` | Server URL when the target is a run ID, `latest` or `prev` and no `--url` is given. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `run` | Export the bees' spans to this OTLP collector (off when unset). |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `run` | `http/protobuf` (default, port 4318) or `grpc` (port 4317). |
| `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | `run` | Traces endpoint used exactly as written (instead of `OTEL_EXPORTER_OTLP_ENDPOINT` + `/v1/traces`). |
| `OTEL_SERVICE_NAME` | `run` | `service.name` of the exported spans (default `beesharp`). |
| `OTEL_SDK_DISABLED` | `run` | `true` turns the export off even with an endpoint. |

## Exit codes

| Code | `run` | `runs …` |
|---|---|---|
| 0 | All bees succeeded. | Inspection succeeded. |
| 1 | At least one bee failed (canceled bees count as failed), the run was stopped with Ctrl+C, or the shutdown program failed. | — |
| 2 | The run could not run: bad arguments, preset, environment or root folder not found, a script the preset needs does not compile, an unknown scenario / preflight / shutdown program or `{{placeholder}}`, the preflight failed, a bad tracing endpoint or protocol. | Invalid arguments, missing file, server connection or HTTP error, unsupported or corrupt bundle, run or bee not found. |
| 3 | — | `runs trace` only: the trace is not stored with the run (not selected, not found in the trace store, no trace store configured); the message gives the likely reason. |

## The embedded host's `run` command

An application built on `Beesharp.Host` (`BeesharpApp`, brain 0058/0061; e.g. `examples/host-app`) starts the web UI
by default and runs headlessly when its first argument is `run`. It uses the host's own scenarios, presets and default
plan, so there is no `--root` and no `--storage`:

```bash
dotnet run --project examples/host-app                                   # web UI
dotnet run --project examples/host-app -- run --preset Smoke --duration 5
dotnet run --project examples/host-app -- run --help
```

| Option | Meaning |
|---|---|
| `-p, --preset <name>` | Preset to run (default: the host's configured default plan). |
| `-e, --env <name>` | Environment whose `{{Key}}` values are filled in. |
| `-d, --duration <seconds>` | Run for this long instead of the plan's duration. |
| `--progress <seconds>` | Progress line interval (default 5, `0` = none). |
| `--logs [level]` | Stream live scenario logs (`trace\|debug\|info\|warn\|error`; default `info`). |
| `--json [file]` | Summary JSON to stdout, or also to `<file>`. |
| `--export <path>` | Export the finished run to a file or directory. |
| `--export-format <fmt>` | `parquet`, `duckdb`, `json` or `csv`. |
| `-h, --help` | Print the help text. |

Exit codes are those of `beesharp run`. The host also honours the `Tracing__*` / `OTEL_*` settings above.

## GitHub Actions example

```yaml
jobs:
  load-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 11.0.x
          dotnet-quality: preview
      - name: Run the preset
        run: >
          dotnet run --project src/Beesharp.Cli -c Release --
          run --preset "Black Friday" --env Staging --root ./load-tests --json summary.json --export run.zip
      - name: Look at the slowest calls
        if: always()
        run: dotnet run --project src/Beesharp.Cli -c Release -- runs calls run.zip --slowest 10
      - name: Keep the run
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: beesharp-run
          path: |
            summary.json
            run.zip
```

The run step fails the job on exit code 1 or 2. Sample output: `docs/screenshots/2026-11-22-cli/`.

