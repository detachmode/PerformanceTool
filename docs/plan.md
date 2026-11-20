# Plan / backlog

Things we want to build next. When an item is implemented, record the decisions in `docs/brain/` and remove
or tick the item here.

## Current phase: UI drafts (user decision)
Until the user is happy with the UI/UX, we build **UI drafts only, with fake data** (no real runner or storage
work). The drafts are now the main pages of the Blazor WebAssembly client (`/`, `/editor`), driven by `FakeRunSource`; they are published to GitHub Pages. Iterate draft → screenshot → user feedback.

## Agent queue
- Done: …, player bar (0027), stable clicking (0026).
- **Done (A):** data side of 0028: retention priority (drop successful first), error dictionary, failed→slow→ok
  ordering and outcome-coloured actor numbers in the Logs popup.
- **Done (B):** popup chart, 0023 update 3: side list instead of nested scroll, group counts in the hovered bar,
  brush-to-zoom with live data, settings panel (zoom strength, max bars), live actors finishing while hovering;
  outcome-coloured actor numbers and ordering in the popup.

- **Done (C):** player bar polish + drill-down counters (0027 update 2, 0029).

- **Done (D):** editor fixes: change detection while typing, missing scrollbars (0019 update).
- **Done:** green card outline when no failed/slow (0018 update).

- **Done (E):** compact statistics on the overview cards (0030); card figures open the popup unfiltered until the popup gets a pre-filter.

- **Next (F, user feedback):** touch gestures on the popup chart (0031).

Open questions for the user (ask one at a time): what counts as "slow"; duration summary; call grouping.

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

## 0b. Typed scenario parameters, generated editor (brain 0007), done
- [x] `Scenario<TParameters>` base; the 4 built-ins migrated (with new options: expected status, auth, think time, repeat, stop on failure).
- [x] Reflection metadata model, generated editor (text/multiline/URL/secret, toggle, enum dropdown, number,
      slider, duration, list, key/value, nested groups), DataAnnotations validation, JSON Schema (`/api/scenarios`).
- [x] Startup check for unsupported types; legacy presets still load.
- [ ] Per-field inline validation messages (today errors are listed above the stats).
- [ ] Conditional fields (e.g. show Token only when Scheme = Bearer).

## 0c. Live scenario tiles + Aspire-inspired restyle (requirement, see brain 0008)
- [x] Runner reports execution-started events and in-flight counts per entry.
- [x] Tile per scenario: chips, title, description, big value + unit, mini bar chart (last ~100 executions), error/drop badges.
- [x] Live overview grid during a run; a tile pulses or glows while its scenario is executing; target vs actual runs/min.
- [x] Click a tile → dialog with parameters, full chart, latency distribution, status codes, errors, recent executions.
- [x] Restyle the app (done except: collapsible left list, chart/table toggle; sort/group menus added) after the Aspire 17 Metrics page (flat purple-tinted dark surfaces, chips, small radius,
      toolbar with icon buttons, collapsible left list, group/sort menus). Re-validate the palette on the new surfaces.

## 0d. Overview + editor pages (brain 0011)
- [ ] Split into a lightweight **overview page** (scenario tiles with little Aspire-style box charts; click → existing details popup) and an **editor page**.
- [ ] Open: where runs are started/stopped; exact mini-chart style.

## 0e. Scenario logging (brain 0012)
- [ ] `ILogger` available to scenarios; new logging scope per run; tool records the logs.
- [ ] Persist logs locally with the run data (CI later: part of the full artifact).
- [ ] UI: log statements visible when inspecting a scenario (details popup). Draft with fake logs first.

## 1. Shared core + run bundles (prerequisite for 2 and 3, see brain 0009, proposed)
- [ ] Extract `Services/*` into `src/PerformanceTool.Core` (scenarios, plan, runner, presets, stats), used by web, CLI and server.
- [ ] Run bundle writer: `manifest.json`, `summary.json`, `results.ndjson(.gz)` (appended live), `summary.md`, `junit.xml`;
      JSON schemas with `schemaVersion`, documented in `docs/`.
- [ ] CI context auto-detection (Azure DevOps, GitHub Actions, local) and user tags.
- [ ] Shared data root (`PerformanceTool:DataDirectory`) with `presets/` and `runs/`; the UI writes every run as a bundle.
- [ ] UI run history: list, open a past run (same charts/tiles), compare two runs.

## 2. CLI for people, harnesses and pipelines (`perftool`)
- [ ] `src/PerformanceTool.Cli` as a .NET tool, `System.CommandLine`; `--json` everywhere; exit codes 0 ok / 1 thresholds failed / 2 invalid input / 3 run error.
- [ ] `scenarios list`, `presets list|show|save|delete`, `run --preset|--plan [--set entry.param=value] [--rate entry=N] [--duration] [--tag k=v]`.
- [ ] `runs list|show|executions|errors|compare|pull` with filters (commit, branch, PR, build, tag, preset, status; scenario, status code, error, min latency, time window, limit).
- [ ] Works locally (data folder) or remotely (`--server` / `PERFTOOL_SERVER`, `PERFTOOL_API_KEY`).
- [ ] `runs pull --from azdo|github --build <id>` downloads CI artifacts via `az` / `gh`.
- [ ] Document the CLI for agents in `AGENTS.md`.

## 3. Results server + API keys (proposal only, not decided)
- [ ] REST API in the web app: `POST /api/runs` (upload a bundle | start a remote run), `GET /api/runs` (filters, paging),
      `GET /api/runs/{id}` (summary), `/executions` (filters, paging), `/errors`, `/compare/{other}`, `/bundle` (zip).
- [ ] API keys: scopes `runs:read`, `runs:write`, `runs:execute`; hashed at rest; created/revoked in the UI; `Authorization: Bearer`.
- [ ] Storage: file system, then an Azure Blob Storage provider.
- [ ] Deployable image + docs (Azure Container Apps / App Service).
- [ ] Later: OIDC / workload identity federation for pipelines instead of static keys.

## 4. CI/CD integration (Azure DevOps first, then GitHub)
- [ ] **Azure DevOps (decided, brain 0010):** the CLI runs in the pipeline and publishes two artifacts per run:
      a full one (all logs and details) and a small summary one (overview, little data).
- [ ] Open: contents of the summary vs full artifact, names, retention, how results appear in Azure DevOps.
- [ ] Thresholds in the plan/preset (`p95 < 300ms`, `errorRate < 1%`, `dropped == 0`; global or per scenario) → exit code + JUnit.
- [ ] Azure DevOps: YAML template (run → publish artifact → PublishTestResults → upload summary → optional push to server → PR comment).
- [ ] GitHub: composite action / reusable workflow (artifact, `$GITHUB_STEP_SUMMARY`, PR comment, optional push).
- [ ] Container image with the CLI (ACR / GHCR).
- [ ] Baseline comparison: fail on regression vs last good run on the default branch.
- [x] CI for this repo itself: build + `dotnet test` on push (`.github/workflows/ci.yml`, brain 0014).

## 5. Agent integration (proposal only, not decided)
- [ ] `perftool mcp`: MCP server over the same API (list_runs, get_run_summary, query_executions, errors, compare_runs, list_scenarios, start_run).
- [ ] PR comment / summary always contains the run id and copy-paste commands for agents to drill down.

## Other ideas
- [x] Unit tests for `LoadRunner` scheduling, legacy preset loading and parameter specs (`tests/PerformanceTool.Tests`).
- [ ] Test project in CI.
- [ ] Ramp-up profiles (rate changes over time) instead of a constant rate.
- [x] `[Secret]` parameters are not stored in presets; `${env:NAME}` placeholders are kept and resolved at run time.
