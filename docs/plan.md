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

- **Done (F, user feedback):** touch gestures on the popup chart (0031): tap selects, hold then drag magnifies, pinch zooms the time range, a swipe scrolls.

- **Done (G, user feedback):** group bars look different from single-actor bars (0032): lighter fill, outline, count badge.
- **Done (H, user feedback):** sideways swipe pans the zoomed time range on touch (0031 update 1).

- **Done (I):** environments (0033): top-bar dropdown, Configure popup, `{{Key}}` placeholders with editor hints, fake data; server `EnvironmentStore`, `/api/environments` (list/get/put/delete) and run-start resolution. Open: the client `ApiEnvironmentSource` (stub), remembering the active environment across reloads, secret flag (Proposed only).
- **Done (J, user feedback):** smooth panning with stable groups, no tap-and-hold, sideways swipe magnifies, edge panning (0031 update 2).
- **Done (J2, user feedback, same agent):** overview strip showing the viewed area (0035).
- **Done (J3, user feedback, same agent):** previous/next failed works with zoom and groups (0036).
- **Done (K):** spawning pattern per scenario entry: linear or bursts (0034), fake data; the server `LoadRunner` supports it too.
- **Done (L, user feedback):** popup chart polish (0037): caret under the selected bar instead of the purple block, magnifier only on the axis strip and sideways swipe on the bars scrolls the zoomed timeline, striped make-up on highlighted groups, even gaps between bars.
- **Done (M, user feedback):** zooming into a burst unfolds it into single actor bars, with a quiet "burst · N actors" label (0038).
- **Done (M2, user feedback):** the fake data starts with a 30 minute finished run (0039).
- **Done (M3, user feedback):** overview strip over the whole width, muted red/amber marks of failed and slow actors, always visible (0035 update 1).
- **Done (N, user feedback):** popup chart navigation bullet proof (0040): no zoom on swipe (root causes fixed), bursts unfold as soon as they fit, redesigned overview strip, persistent selection pill. Open for the user: review the Proposed design and the verdict on compressed time gaps.
- **Done (O, user feedback):** slowness as a colour gradient from green at typical time to amber (0041): ratio per actor (one byte), four steps, groups take their worst actor, "1.3x typical" drills down to the calls. Open for the user: review the Proposed details.
- **Done (P, user feedback):** a burst that does not fit splits into even groups (nice sizes 1, 2, 5, 10, ...) that refine while zooming until every actor has a bar; failed and slow actors stay in their group, previous/next failed steps group by group (0043). Debug URL `?burst=100`.
- **Next (Q, user feedback):** the lilac selection marker as one robust piece (0044).
- **Done (T, user feedback):** bees in flight when a run is stopped are kept as canceled, with their logs and calls (the cut-off call is failed), shown and counted like failed, error "Canceled: run stopped" (0053). Follow-up: bee polling by list position instead of highest sequence.
- **Done (U, user request):** module split (0054), after T, in steps U1 (rename + Contracts; **done**, see 0054 notes), U2 (Engine, Scripting, Storage; drop SQLite; **done**, see 0054 notes), U3 (command-line tool, `docs/cli.md`; **done**, see 0054 notes), U4 (client folders by topic, one test project per project; **done**, see 0054 notes). Follow-ups: split `ScenarioDetailDialog.razor`, `Overview.razor` and `dock-magnify.js` into smaller pieces; the run state says "Finished" before the final values are stored (tests wait with `WaitUntilFinishedAsync`; consider setting Finished after the storage writes).
- **Done (S1, user request):** Environment tab UI draft with fake data (0052): static values of the selected environment, run bag and entry bags, live updates while running, final values for finished runs.
- **Done (S2, user request):** shutdown program that always runs at the end (0052) and runtime data (run bag, entry bags, writer tracking, final values in run storage) in the server runner; Environment tab works in Live mode. Preflight and shutdown results + logs are persisted with the run (DuckDB + InMemory), shown as collapsed strips on Overview, and queryable on `/logs`. Follow-up: assistant tools for runtime data (with secret masking).
- **Done (R, user request):** AI assistant (0047-0051), in three steps:
  - R1 (done): the server owns the Live plan and presets (plan workspace + REST, `PresetStore`); `ApiRunSource` uses them.
  - R2 (done): `src/Beesharp.Ai.Contracts` + `src/Beesharp.Ai` (Microsoft.Extensions.AI, env var config `AI__*`, ports,
    tools (no approval, 0048), sessions, `AddBeesharpAi`/`MapBeesharpAi`); host adapters; tests with a fake `IChatClient`.
  - R2/R3 also: show tools and chat cards (0049): logs box with Show more full screen, bees, bee, scenario/run, plan changes, and picked bees together on one time line like the popup chart (0049 update 1).
  - R3 (done): chat side panel in the client (Live mode only, only when configured), screenshots against a local
    OpenAI-compatible stub.

- **Done (V, user request):** scenario steps UI draft (incl. a step that repeats; screenshots in `docs/screenshots/2026-11-22-scenario-steps/`) with fake data (0056): stepper in the bee detail, "step N of M" for bees in flight, failed step, bees failed per step in the scenario popup.
- **Done (W, user request):** scenario steps backend + Live mode (0056): C# 15 `union` step machine (`Scenario<TParameters, TStep>`, `Scenario.DefineSteps`), automatic HTTP call recording in load runs, DuckDB schema v2 migration, REST/CLI/AI/Client Live wiring, and `src/Beesharp/scripts/checkout-steps.cs`.

- **Done (X, user request):** UI polish (2026-11-23): one calm look, new overview cards, step list (0056), fixes; screenshots `docs/screenshots/2026-11-23-ui-polish-review/`.
- **Done (Y1, user decision 0059):** the scenario as its own page `/scenario/{n}` (three columns: steps, chart and bees, the selected bee on the right) instead of the popup; the assistant's bee set keeps a dialog around the same view; Live mode no longer loses bees that finish out of order; screenshots `docs/screenshots/2026-11-24-scenario-page/`. Phone layout still to be drafted.
- **Done (Y2, user decision 0059):** Logs page (filter column with counts, full-width search, removable filter chips; Fake, live, finished and stored runs, server-side paging, counts and step filter via `GET /api/runs/{id}/logs/facets`), Editor (toolbar + Try column), Environment (one centred column), and a copy-all-logs button wherever logs show; screenshots `docs/screenshots/2026-11-24-logs-editor-environment/`. The logs popup is gone (entry points link to `/logs?...`). Follow-up: preflight and shutdown output is not stored with a run, so a stored run shows no program output (see 0059 implementation notes).
- **Done (Z, user request 0060):** Calls analysis page (`/calls`, 2-column master-detail with duration-over-time chart, clickable histogram, ad-hoc ms filter, slow-first call list + raw URL inspector), automatic tracking across all `HttpClient`s (`DiagnosticListener` + `IHttpMessageHandlerBuilderFilter` + `RecordingHttpHandler`), auto-detected endpoint normalization + adaptive slug collapse (`EndpointNormalizer`, `EndpointGrouper`), and per-scenario/per-call slow thresholds (`CallThresholds`, `.SlowAbove(...)`, `ctx.CallScope(...)`) unified with `2× typical` in both Live and Fake mode. Screenshots `docs/screenshots/2026-11-24-calls-analysis/`.

- **Done (Y3, user decision 0059):** the phone layouts (up to 760 px wide, or a phone turned sideways): bottom tab bar, slim run bar above it (sheet with the full player), top bar with title and mode, ⋯ for environment/settings; overview full cards; scenario page as tabs Steps · Chart · Bees (`?tab=`), the bee as a bottom sheet (`?sheet=1`, Back closes it); Logs with search, chips and a filter sheet; Editor as tabs Code · Try; Environment in one column. Screenshots `docs/screenshots/2026-11-25-phone-layouts/`.
- **Done (AA, user request 0063):** every actor is the root of its own distributed trace (actor → step → call spans, sampled `traceparent` to the services under test on all three HTTP paths), trace and span IDs stored per call (DuckDB schema 5, REST, exports) and shown in the call inspector and the bee's Calls tab, optional OTLP export (`Tracing:OtlpEndpoint` / `OTEL_EXPORTER_OTLP_ENDPOINT`) in the new `Beesharp.Tracing` module. Screenshots `docs/screenshots/2026-11-25-trace-parent/`. Next (not started): fetch a trace by ID from Jaeger/Elastic (`examples/tracing`) into the call inspector.

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
