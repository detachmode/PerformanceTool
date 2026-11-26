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
  [`compare`](#beesharp-runs-compare) · [`timeline`](#beesharp-runs-timeline)
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
- **Trace IDs.** Runs store the W3C trace and span ID of every call (brain 0063) and exported bundles contain them;
  the `runs` commands do not print them yet (planned with `runs trace`, brain 0065).

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

## Planned (brain 0065)

`beesharp runs trace` (one bee's or call's distributed trace, fetched from the trace store into the run) and
`beesharp runs hotspots` (where the time of slow calls went, per endpoint) — Jaeger first.
