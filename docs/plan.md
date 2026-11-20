# Plan / backlog

Things we want to build next. When an item is implemented, record the decisions in `docs/brain/` and remove
or tick the item here.

## Done
- [x] Blazor Server app with a live bar chart, one bar per execution (brain 0001, 0002)
- [x] Scenarios defined in code, mixed with parameters and runs per minute (brain 0003, 0004)
- [x] Save/load presets (brain 0005)

## 0. Interactive UX (requirement, see brain 0006)
Everything that shows data is clickable, inspectable, filterable and isolatable.
- [ ] Click a bar → details panel for that execution (scenario, params, timing, status, error, bytes, start time).
- [ ] Click a legend entry / table row / scenario card → toggle that scenario; "only" (or Alt+click) isolates it.
- [ ] Click a stat tile (Errors, Dropped, p95…) → filter to the executions behind it (e.g. failed only, slower than p95).
- [ ] Filters for status code, success/failure and latency range; drag on the chart to select a time window.
- [ ] Active filters shown as removable chips; chart, stats and table all use the same filtered set.
- [ ] Bar hover: rich tooltip instead of the native `title`; highlight the same scenario's bars.
- [ ] Error grouping: click an error message → all executions with that error.
- [ ] Keyboard access (focusable bars/rows, Esc closes panels); works during a live run.

## 1. Shared core + persisted run history (prerequisite for 2 and 3)
Today results only live in the browser circuit. The CLI and CI need the same data as the UI.
- [ ] Extract `Services/*` (scenarios, `LoadPlan`, `LoadRunner`, `PresetStore`, stats) into a class library
      `src/PerformanceTool.Core`, used by the web app and the CLI. Scenarios live there (or in a separate
      scenarios library) so every host has the same catalog.
- [ ] Persist each run: plan snapshot, start/end time, per-execution results, per-scenario summary. Same
      file-based approach as presets (e.g. `data/runs/<timestamp>-<name>/run.json` + `results.ndjson`),
      under one shared data root (`PerformanceTool:DataDirectory`) with `presets/` and `runs/` inside.
- [ ] UI: run history page (list, open a past run, compare two runs). Runs started from the CLI show up too.
- [ ] Optional: the UI picks up a run that is in progress from another process (tail `results.ndjson`).

## 2. CLI for people and coding harnesses
Goal: an agent (Claude Code etc.) can do everything the UI can, on the same data.
- [ ] `src/PerformanceTool.Cli` as a .NET tool (`perftool`), using `System.CommandLine`.
- [ ] Commands:
  - `perftool scenarios list` — ids, descriptions, parameter schemas
  - `perftool presets list | show <name> | save <name> --file plan.json | delete <name>`
  - `perftool run --preset <name>` or `--plan plan.json`, overrides like `--duration 60`,
    `--set "<entry>.url=https://..."`, `--rate "<entry>=120"`
  - `perftool runs list | show <id>` — read the stored history
- [ ] `--json` on every command: stable, machine-readable output (schema documented) so harnesses can parse it.
      Default output is a readable table plus a live progress line.
- [ ] Same data directory as the web app (shared config), so presets/runs created in one show up in the other.
- [ ] Meaningful exit codes: 0 ok, 1 thresholds failed, 2 invalid input/config, 3 run error.
- [ ] Document the CLI in `AGENTS.md` so agents can find it.

## 3. Running in CI/CD pipelines
- [ ] Pass/fail **thresholds** stored in the preset/plan, e.g. `p95 < 300ms`, `errorRate < 1%`,
      `dropped == 0`, per scenario or global. `perftool run` exits 1 when any fails.
- [ ] Report outputs: JSON summary, JUnit XML (one test case per threshold, so CI shows failures natively),
      Markdown summary (for `$GITHUB_STEP_SUMMARY` / PR comments).
- [ ] Container image (`Dockerfile`) with the CLI, and an example GitHub Actions workflow
      (`.github/workflows/perf.yml`) that runs a preset against a deployed environment and uploads reports.
- [ ] Optional: compare with a baseline run and fail on regression (e.g. p95 +20%).
- [ ] CI for this repo itself: build + tests on push.

## Other ideas
- [ ] Unit tests for `LoadRunner` scheduling, preset round-trips and parameter validation.
- [ ] Ramp-up profiles (rate changes over time) instead of a constant rate.
- [ ] Secrets in parameters (tokens) shouldn't be stored in plain preset files: support `${env:NAME}` placeholders.
