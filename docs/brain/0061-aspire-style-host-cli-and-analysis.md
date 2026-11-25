# 0061. Aspire-style host CLI and `.zip` run bundle inspection

- Status: Proposed
- Date: 2026-11-25

## What
A CLI design focused first on **offline inspection of exported run `.zip` bundles**, and second on an **Aspire-style two-layer host CLI** (`Beesharp.Host` + global `beesharp` tool) for live instances and compiled-scenario hosts:

1. **Offline `.zip` run bundle inspection in `Beesharp.Cli` (primary focus, Phase 1)**:
   - Inspect exported run bundles (`.zip` Parquet bundles, plus `.duckdb` / `.json` exports) directly from any directory—no running server, no `.csproj`, and **no touching a live server's `data/beesharp.duckdb`** (avoiding DuckDB cross-process lock conflicts completely).
   - Internally queries the bundle without loading the whole run into RAM:
     - **Parquet `.zip` bundles**: extracts only the `.parquet` table(s) needed for the command (e.g., `summary`/`errors` skip `logs.parquet` completely) to a temp folder and queries them directly with DuckDB `read_parquet(...)` (streaming row-groups, column projection, predicate pushdown, bounded memory with disk spill).
     - **Exported `.duckdb` files**: opened directly with `ACCESS_MODE=READ_ONLY` (no copy).
     - **Exported `.json` files**: parsed directly.
   - Subcommands (all supporting a pure boolean `--json` flag for untruncated stdout JSON, and `--url <url>` / `BEESHARP_URL` for live instances with `latest`/`prev` aliases):
     - `beesharp runs list --url <url> [--limit <n>] [--json]` — list stored runs on a live instance (`GET /api/runs`).
     - `beesharp runs summary <file.zip|runId|latest> [--url <url>] [--json]` — run metadata, total/failed/canceled/slow bees, per-scenario & per-step failure counts, and min/p50/p95/max durations.
     - `beesharp runs errors <file.zip|runId|latest> [--url <url>] [--json]` — grouped, untruncated errors + sample bee sequence.
     - `beesharp runs calls <file.zip|runId|latest> [--url <url>] [--entry <n>] [--slow] [--failed] [--min-ms <ms>] [--search <q>] [--json]` — endpoint latency breakdown and slow/failing calls (reusing `GetCallEndpointsAsync`, brain 0060).
     - `beesharp runs logs <file.zip|runId|latest> [--url <url>] [--bee <seq>] [--entry <n>] [--step <n>] [--level <lvl>] [--search <q>] [--offset <n>] [--limit <n>] [--json]` — filtered log lines (reusing `QueryLogsAsync`).
     - `beesharp runs bee <file.zip|runId|latest> <seq> [--url <url>] [--json]` — full step visits, HTTP calls, status codes, and logs for one bee (`GetBeeDetailAsync`).
     - `beesharp runs compare <a.zip|runA|prev> <b.zip|runB|latest> [--url <url>] [--json]` — latency and error-rate regression diff between two runs.
   - Works seamlessly with `beesharp run ... --export run.zip` in CI and with bundles downloaded from the UI or CI artifacts (Azure DevOps / GitHub Actions).

2. **Core host subcommands inside `Beesharp.Host` (`BeesharpApp.RunAsync(args)`) (Phase 2)**:
   - Extend `BeesharpApp`'s headless CLI mode beyond `run` to support:
     - `scenarios list` — list compiled and script scenarios registered in the host (without opening DuckDB).
     - `presets list` — list disk and code-defined presets (without opening DuckDB).
     - `test-run <scenario> [--env <name>]` — single-bee smoke check (exit codes `0` ok, `1` failed, `2` could not run).
     - `run --preset <name> [--env <name>] [--duration <s>] [--export <path>] [--json]` — full headless load run (already partially implemented via `HeadlessRunner`).
   - Keep `--json` as a pure boolean stdout flag across all subcommands (use `--json-file <path>` when writing a JSON file on `run`).

3. **Global `beesharp` CLI wrapper & live-instance routing (`Beesharp.Cli`) (Phase 2)**:
   - **Reliable project discovery** (deterministic order):
     1. Explicit `--project <path>` flag or `BEESHARP_PROJECT` env var.
     2. Marker file (`.beesharp/cli.json` or `beesharp.json`, walking up from CWD to repo root) specifying `project` and/or `url`.
     3. Bounded `*.csproj` scan from CWD (skipping `bin/`, `obj/`, `.git/`, `node_modules/`, `artifacts/`, `TestResults/`) for `<PackageReference Include="Beesharp.Host" />` or `<ProjectReference Include="...Beesharp.Host.csproj" />`. If 1 match, use it; if 0 or >1, fail clearly (or prompt in an interactive TTY).
     4. Build with stdout redirected to stderr (`dotnet build --nologo -v quiet`) and invoke via `dotnet exec <TargetPath> <args>` (or `dotnet run --no-build`) so build output never corrupts `--json` stdout.
   - **Running-instance detection & HTTP routing** (Aspire-style runtime discovery file, no port scanning, no direct DuckDB reads against a live server):
     - On server startup, `BeesharpApp` writes `~/.beesharp/instances/<hash>.json` (keyed by canonical project/root path) containing `pid`, `instanceId`, `projectPath`, `urls`, and `startedUtc`, and removes it on graceful shutdown.
     - Lightweight `GET /api/info` endpoint returns `pid`, `instanceId`, `projectPath`, and `startedUtc` (preferring loopback `http://` to avoid dev-cert issues) so probes verify exact instance identity.
     - `beesharp ps [--json]` — scans `~/.beesharp/instances/*.json`, verifies PID + `GET /api/info` (~300 ms timeout), cleans up stale files, and lists all live local instances.
     - `beesharp status [--url <url>] [--json]` — queries `GET /api/runs/current` on the targeted or auto-discovered instance and reports state (`Idle`, `Running`, `Paused`, `Finished`), elapsed time, and counts.
     - Live `runs` queries against a running instance (`--url` / `BEESHARP_URL` or auto-discovered instance) go over HTTP—never opening `data/beesharp.duckdb`.
   - **Publishing & installation**: `Beesharp.Cli` is configured as a .NET tool (`PackAsTool=true`, `ToolCommandName=beesharp`) and will be published to NuGet.org alongside `Beesharp.Core` and `Beesharp.Host` via `.github/workflows/nuget.yml` on `v*` tags (`dotnet tool install -g Beesharp.Cli`).

## Phased Rollout
1. **Phase 1 (offline `.zip` inspection in `Beesharp.Cli`)**: `runs summary`, `runs errors`, `runs calls`, `runs logs`, `runs bee`, and `runs compare` querying exported `.zip` (lazy `read_parquet(...)`), `.duckdb` (`READ_ONLY`), and `.json` run bundles, with `--json` everywhere.
2. **Phase 2 (Aspire-style host CLI & live HTTP routing)**: `Beesharp.Host` subcommands (`scenarios list`, `presets list`, `test-run`), `GET /api/info`, `~/.beesharp/instances/` discovery, `beesharp ps|status`, `--url` HTTP routing, and publishing `Beesharp.Cli` in `nuget.yml`.

## Why
- **No DuckDB locking pain**: DuckDB forbids even `ACCESS_MODE=READ_ONLY` from another process while a server holds the DB open in read-write mode. Querying exported `.zip` bundles directly via lazy `read_parquet(...)` (and exported `.duckdb` files in `READ_ONLY` mode) works reliably everywhere—locally, in CI, and for AI agents—with zero lock conflicts, bounded memory even on huge runs, and no server required.
- **Compiled scenarios live in the host project, not in a global CLI binary**: a standalone global CLI cannot load a user's `Scenario<TParameters, TStep>`, DI singletons, or code presets without delegating to the host project (just like `aspire` delegates to `AppHost`).
- **Incremental value**: shipping `.zip` inspection first immediately unlocks CI artifact and local export analysis for humans and agents before adding host delegation and live-instance discovery.

## Consequences
- `Beesharp.Cli` reads exported `.zip` (via lazy `read_parquet(...)` views) / `.duckdb` (`READ_ONLY`) / `.json` run bundles directly, and **never** opens a live server's `data/beesharp.duckdb` file.
- `Beesharp.Host` (`BeesharpApp`) remains the canonical home for compiled-scenario headless execution (`run`, `test-run`, `scenarios list`, `presets list`) and exposes HTTP endpoints for live-instance inspection.
- Existing `beesharp run --preset ... --root ...` behavior remains compatible.
