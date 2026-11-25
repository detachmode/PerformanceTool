# 0061. Aspire-style host CLI and run analysis commands

- Status: Proposed
- Date: 2026-11-25

## What
A two-layer CLI architecture (matching the `aspire` / `AppHost` pattern) so CI pipelines, developers, and AI agents can discover, trigger, and analyze runs for host projects that register compiled scenarios, DI services, preflights, shutdowns, and code presets in `Program.cs`:

1. **Core subcommands inside `Beesharp.Host` (`BeesharpApp.RunAsync(args)`)**:
   - Extend `BeesharpApp`'s headless CLI mode beyond `run` to support:
     - `scenarios list` — list compiled and script scenarios registered in the host.
     - `presets list` — list disk and code-defined presets.
     - `test-run <scenario> [--env <name>]` — single-bee smoke check.
     - `run --preset <name> [--env <name>] [--duration <s>]` — full headless load run (already partially implemented via `HeadlessRunner`).
     - `runs list [--take <n>]` — list stored runs.
     - `runs summary <runId>` — min/p50/p95/max and failure counts per scenario & step.
     - `runs errors <runId>` — grouped, untruncated errors + sample bee sequence.
     - `runs bee <runId> <seq>` — full step visits, HTTP calls, status codes, and logs for one bee.
     - `runs compare <runA> <runB>` — latency and error-rate regression diff.
   - Add a consistent `--json` output flag across all subcommands for agent- and CI-friendly machine consumption (without truncation).
   - `runs` analysis subcommands can open `data/beesharp.duckdb` in read-only mode (`ACCESS_MODE=READ_ONLY`) without starting Kestrel.

2. **Optional thin global `beesharp` CLI tool (`Beesharp.Cli`)**:
   - **Reliable project discovery** (deterministic order):
     1. Explicit `--project <path>` flag or `BEESHARP_PROJECT` env var.
     2. Marker file (`.beesharp/cli.json` or `beesharp.json`, walking up from CWD to repo root) specifying `project` and/or `url`.
     3. `*.csproj` scan from CWD (skipping `bin/`, `obj/`, `.git/`, `node_modules/`) for `<PackageReference Include="Beesharp.Host" />` or `<ProjectReference Include="...Beesharp.Host.csproj" />`. If 1 match, use it; if 0 or >1, fail clearly (or prompt in an interactive TTY).
     4. Invoke via `dotnet run --project <csproj> [--no-build] -- <args>` (or query MSBuild `TargetPath` and run `dotnet exec <TargetPath> <args>`).
   - **Running-instance detection & commands** (Aspire-style runtime discovery file, no port scanning):
     - On server startup, `BeesharpApp` writes a small JSON file keyed by a hash of the canonical project/root path (e.g. `~/.beesharp/instances/<hash>.json`) containing `pid`, `projectPath`, `urls`, and `startedUtc`, and removes it on graceful shutdown.
     - `beesharp ps [--json]` — scans `~/.beesharp/instances/*.json`, verifies PID liveness + `GET /api/scenarios` (~300 ms timeout), cleans up stale files, and lists all live local instances.
     - `beesharp status [--url <url>] [--json]` — queries `GET /api/runs/current` on the targeted or auto-discovered instance and reports its state (`Idle`, `Running`, `Paused`, `Finished`), elapsed time, and counts.
     - When other commands run without `--url`, they check `--url` / `BEESHARP_URL` first, then the matching live instance file (routing to its HTTP API if alive), and otherwise fall back to headless execution or read-only DuckDB.
   - **Offline analysis**: `runs` subcommands can open `data/beesharp.duckdb` directly in `ACCESS_MODE=READ_ONLY` without starting Kestrel.
   - **Publishing & installation**: `Beesharp.Cli` is already configured as a .NET tool (`PackAsTool=true`, `ToolCommandName=beesharp`) and is published to NuGet.org alongside `Beesharp.Core` and `Beesharp.Host` via `.github/workflows/nuget.yml` on `v*` tags (installed via `dotnet tool install -g Beesharp.Cli`).

## Phased Rollout
1. **Phase 1 (inside `Beesharp.Host`)**: `scenarios list`, `presets list`, `test-run`, `--json` on `run`, and `runs list|summary|errors`.
2. **Phase 2 (deep inspection & global tool)**: `runs bee`, `runs compare`, and the thin global `beesharp` wrapper (`ps`, `status`, project/instance discovery, `--url` HTTP routing, and publishing `Beesharp.Cli` in `nuget.yml`).

## Why
- **Compiled scenarios live in the host project, not in a global CLI binary**: a standalone global CLI cannot load a user's `Scenario<TParameters, TStep>`, DI singletons, or code presets without delegating to the host project (just like `aspire` delegates to `AppHost`).
- **Incremental rollout**: implementing `--json`, `test-run`, and `runs` subcommands directly inside `Beesharp.Host` immediately enables `dotnet run --no-build -- <cmd> --json` in CI and agent workflows; the thin global `beesharp` wrapper can then sit cleanly on top.

## Consequences
- `Beesharp.Host` (`BeesharpApp`) becomes the canonical home for headless execution and run-analysis CLI subcommands, reusing `RunManager`, `IRunStorage`, and `ScenarioCatalog`.
- `Beesharp.Cli` (`beesharp` global tool) stays thin: project discovery/build/delegation, `--url` HTTP client mode, and read-only DuckDB inspection.
- Existing `dotnet run --project src/Beesharp.Cli -- run ...` behavior remains compatible while the host-first subcommands are rolled out.
