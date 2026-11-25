# The command-line tool (`beesharp`)

`src/Beesharp.Cli` runs presets **without the web server and the UI** for CI pipelines (brain 0054) and inspects
exported run bundles offline without locking a live server's database (brain 0061).

## Install or run

Install `Beesharp.Cli` from NuGet as a global .NET tool (the command is `beesharp`):

```bash
dotnet tool install --global Beesharp.Cli
beesharp run --preset "Black Friday" --env Staging
```

To update an existing installation:

```bash
dotnet tool update --global Beesharp.Cli
```

Or install from a local checkout:

```bash
dotnet pack src/Beesharp.Cli -c Release -o ./nupkg
dotnet tool install --global Beesharp.Cli --add-source ./nupkg
```

Or run directly from source without installing:

```bash
dotnet run --project src/Beesharp.Cli -- run --preset "Black Friday" --env Staging --root ./load-tests
```

Output is plain text without colour codes, readable in CI logs.

---

## `beesharp run` — execute a preset headlessly

| Option | Meaning |
|---|---|
| `-p, --preset <name\|file>` | The preset's name (from `<root>/presets/`) or the path of a preset `.json` file. Required. |
| `-e, --env <name>` | The environment (`<root>/environments/`) whose `{{Key}}` values are filled in. Unknown keys refuse to start. |
| `--root <folder>` | Holds `presets/`, `environments/`, `scripts/` (with `preflight/`, `shutdown/`) and `data/`. Default: the current directory. |
| `--storage duckdb\|memory` | `duckdb` (default) saves the run to `<root>/data/beesharp.duckdb`; start the server on the same root and the run shows in the UI's history. `memory` saves nothing. |
| `-d, --duration <seconds>` | Run for this long instead of the preset's duration. |
| `--progress <seconds>` | Progress line interval (default 5, `0` = none). |
| `--logs [level]` | Stream live scenario logs (`trace\|debug\|info\|warn\|error`; default `info`). |
| `--json [file]` | Write summary JSON to stdout (suppresses chatter), or also write JSON to `<file>`. |
| `--export <path>` | Export the finished run to a file or directory (e.g. `run.zip` or `exports/`). |
| `--export-format <fmt>` | Export format: `parquet`, `duckdb`, `json`, or `csv` (inferred from `--export` when omitted). |

### `beesharp run` exit codes

| Code | Meaning |
|---|---|
| 0 | All bees succeeded. |
| 1 | At least one bee failed (canceled bees count as failed), the run was stopped with Ctrl+C, or the shutdown program failed. |
| 2 | The run could not run: bad arguments, preset, environment or root folder not found, a script the preset needs does not compile, a scenario / preflight / shutdown program is unknown, an unknown `{{placeholder}}`, the preflight failed. |

Ctrl+C stops the run like Stop in the UI: bees in flight become canceled, the shutdown program still runs, the run is saved.

### Sending traces to a collector (brain 0062)

Every bee is the root of its own trace and every call carries a sampled `traceparent`, whatever you configure; the trace IDs are stored with the run. To also **export** the bees' spans (bee → step → call) over OTLP, set the standard OpenTelemetry environment variables; without an endpoint nothing is exported:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 beesharp run --preset smoke        # http/protobuf (default)
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 OTEL_EXPORTER_OTLP_PROTOCOL=grpc beesharp run --preset smoke
```

`OTEL_SERVICE_NAME` (default `beesharp`) names the service. A bad endpoint or protocol exits with code 2. The spans are flushed when the run ends; a collector that is down costs at most 3 seconds there and never fails the run.

---

## `beesharp runs` — inspect exported run bundles or live servers

Inspect exported `.zip` (Parquet bundles), `.duckdb`, or `.json` run bundles directly from any directory—or query a running Beesharp instance over HTTP with `--url <url>` (or `BEESHARP_URL`)—without ever touching a live `data/beesharp.duckdb`.

- **Lazy `.zip` querying:** extracts only the `.parquet` table(s) needed by each command into a temporary directory and queries them via DuckDB `read_parquet(...)` (e.g., `summary`, `errors`, `calls`, and `compare` never extract `logs.parquet`, and `errors`/`compare` never extract `calls.parquet`).
- **Read-only `.duckdb`:** opens exported `.duckdb` files directly with `ACCESS_MODE=READ_ONLY` (no copy).
- **Remote `--url` / `BEESHARP_URL`:** pass `--url <url>` (or set `BEESHARP_URL` when the target is a run ID or alias rather than a local bundle file). Supports exact run IDs plus `latest` (most recent) and `prev` (second most recent).
- **1-based numbering:** scenario entries, steps, and bees match the UI (`--entry 1`, `--step 1`, bee `1` or `B1`).
- **`--json`:** a pure boolean stdout flag across all `runs` subcommands emitting untruncated camelCase JSON.

### Subcommands

```bash
beesharp runs list [--url <url>] [--limit <n>] [--json]
beesharp runs summary <file|runId|latest|prev> [--url <url>] [--json]
beesharp runs errors <file|runId|latest|prev> [--url <url>] [--json]
beesharp runs calls <file|runId|latest|prev> [--url <url>] [--entry <n>] [--scenario <id>] [--step <n>] [--by-step] [--slowest <n>] [--endpoint <ep>] [--slow] [--failed] [--min-ms <ms>] [--search <q>] [--json]
beesharp runs logs <file|runId|latest|prev> [--url <url>] [--bee <n|Bn>] [--entry <n|preflight|shutdown|host>] [--step <n>] [--level <lvl>] [--search <q>] [--offset <n>] [--limit <n>] [--json]
beesharp runs bee <file|runId|latest|prev> <bee> [--url <url>] [--json]
beesharp runs compare <fileA|runA|prev> <fileB|runB|latest> [--url <url>] [--json]
beesharp runs timeline <file|runId|latest|prev> [--url <url>] [--bucket <sec>] [--entry <n>] [--scenario <id>] [--json]
```

| Subcommand | Purpose |
|---|---|
| `list` | List stored runs on a running server (`--url` or `BEESHARP_URL`). |
| `summary <target>` | Run metadata (`runId`, `state`, `startedUtc`, `elapsedSeconds`, `error`), total/ok/failed/canceled/slow bees, per-scenario and per-step counts + call latency (`min`/`p50`/`p95`/`max`/`avg`) & slow counts, and overall `min`/`p50`/`p95`/`max` durations. |
| `errors <target>` | Grouped, untruncated error strings with counts, affected scenario entries/steps, and sample bee numbers. |
| `calls <target>` | Endpoint latency breakdown (brain 0060 normalization/grouping), slow thresholds, and slow/failing call counts. Use `--scenario <id>`, `--step <n>`, or `--by-step` to isolate shared URLs, or `--slowest <n> [--endpoint <ep>]` to list the slowest outlier calls with bee IDs, timing splits, and captured headers. |
| `logs <target>` | Filtered, paged log lines (`--level` accepts comma-separated `trace`, `debug`, `info`, `warn`, `error`). |
| `bee <target> <bee>` | Full step visits, HTTP calls (with display labels, optional TTFB/read timing split, and captured headers), status codes, errors, and logs for one bee (`1` or `B1`). |
| `compare <targetA> <targetB>` | Overall and per-scenario latency (`p50`/`p95`/`max`) and error-rate / failed-bee / slow-bee regression diff between baseline `targetA` and candidate `targetB`. |
| `timeline <target>` | Time-bucketed concurrency (`active bees`), `req/s`, `p50`/`p95`/`max` latency, and slow/failed counts (`--bucket <seconds>`, default 5 s). |

### `beesharp runs` exit codes

| Code | Meaning |
|---|---|
| 0 | Inspection succeeded. |
| 2 | Invalid arguments, missing file, server connection/HTTP error, unsupported/corrupt bundle, or requested run/bee not found. |

### Examples

```bash
# Run a preset in CI and export a Parquet .zip bundle
beesharp run --preset "Black Friday" --env Staging --export ./artifacts/run.zip

# Inspect the exported bundle offline
beesharp runs summary ./artifacts/run.zip
beesharp runs errors ./artifacts/run.zip --json
beesharp runs calls ./artifacts/run.zip --slow --min-ms 250
beesharp runs logs ./artifacts/run.zip --entry 1 --step 2 --level warn,error --limit 50
beesharp runs bee ./artifacts/run.zip B4 --json
beesharp runs compare ./artifacts/baseline.zip ./artifacts/candidate.zip

# Inspect a running Beesharp server over HTTP
export BEESHARP_URL=http://localhost:5000
beesharp runs list
beesharp runs summary latest
beesharp runs compare prev latest --json
beesharp runs calls latest --url http://localhost:5000 --slow
```

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
      - name: Keep the run
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: beesharp-run
          path: |
            summary.json
            run.zip
```

The step fails the job on exit code 1 or 2. Sample output: `docs/screenshots/2026-11-22-cli/`.
