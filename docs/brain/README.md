# Brain — decision records

This folder records **what** we built and **why**, so later changes (by people or coding agents) can check
earlier reasoning instead of guessing it or undoing it by accident.

## Rules

- **Read the relevant records before changing an area.** If a change contradicts a record, either follow the
  record or write a new record that supersedes it. Don't silently diverge.
- **Add a record for every decision that someone would otherwise ask "why is it like this?" about**: new
  architecture, a dependency, a storage format, a trade-off, a rejected alternative.
- Records are append-only history. To change a decision, add a new record and set the old one's status to
  `Superseded by NNNN`. Fixing typos or adding links is fine.
- Mark records that are only Claude's proposal as `Proposed`. Only the user turns them into `Accepted`.
- Keep them short. A record that takes more than two minutes to read is too long.

## Template

Copy into `NNNN-short-title.md` (next free number):

```markdown
# NNNN. Title

- Status: Proposed | Accepted | Superseded by NNNN
- Date: YYYY-MM-DD

## What
The decision, in a few sentences. Name the files/types it lives in.

## Why
The problem and forces behind it. Alternatives considered and why they lost.

## Consequences
What follows from it: limits, things to keep in mind, follow-up work.
```

## Index

| # | Decision | Status |
|---|----------|--------|
| [0001](0001-blazor-server-on-dotnet-11.md) | Blazor Server (interactive server rendering) on .NET 11 | UI part superseded by 0015 |
| [0002](0002-live-bar-chart-in-html-css.md) | Live bar chart in plain HTML/CSS, one bar per execution, coloured per scenario | Colour part superseded by 0018 |
| [0003](0003-code-defined-scenarios.md) | Scenarios are defined in C# code, configured in the UI | Accepted |
| [0004](0004-open-model-rate-scheduling.md) | Open-model scheduling: runs per minute per scenario, drop when saturated | Accepted |
| [0005](0005-presets-as-json-files.md) | Presets stored as one JSON file each | Accepted |
| [0006](0006-ux-everything-clickable-and-inspectable.md) | UX principle: everything clickable, inspectable, filterable, isolatable | Accepted |
| [0007](0007-scenario-editor-derived-from-typed-spec.md) | Scenario editor UI generated from a typed parameter spec (enum → dropdown, bool → toggle, …) | Accepted, implemented |
| [0008](0008-live-scenario-tiles-overview.md) | Live scenario tiles with mini charts, activity highlighting, detail popup; Aspire-inspired styling | Accepted, implemented |
| [0009](0009-agent-access-to-ci-runs.md) | Agents inspect CI runs: run bundles, artifact + central server (API key), one CLI/API (+ MCP) | Proposed, partly decided in 0010 |
| [0010](0010-azure-devops-pipeline-artifacts.md) | Azure DevOps: tool runs in the pipeline; results as a full artifact + a small summary artifact | Accepted, implemented (UI, Pages) |
| [0011](0011-overview-and-editor-pages.md) | Separate lightweight overview page (tiles with Aspire-style box charts + details popup) and editor page | Accepted, implemented (UI, Pages) |
| [0012](0012-scenario-logging.md) | Scenarios log via ILogger with one scope per actor (one bar); logs persisted (CI: full artifact) and shown when inspecting a scenario | Accepted, implemented (UI, Pages) |
| [0013](0013-call-durations-are-first-class.md) | Call durations inside each actor are a first-class result; recorded via an HttpClient DelegatingHandler; shown as stacked actor bars (up to ~50 calls) | Accepted, implemented (UI, Pages) |
| [0014](0014-ci-on-github-actions.md) | GitHub Actions CI: restore, build, test (TRX artifact + summary) on every push/PR | Accepted |
| [0015](0015-webassembly-client-rest-api-github-pages.md) | Blazor WebAssembly client + REST API server; fake-data demo on GitHub Pages | Accepted, implemented (UI, Pages) |
| [0016](0016-calm-ui-less-information.md) | Calm UI: show less, reveal on demand; avoid information overload | Accepted |
| [0017](0017-easy-navigation-runs-scenarios-actors-logs.md) | Easy navigation runs → scenarios → actors → logs; filter logs by scenario or actor in a separate Logs popup | Accepted |
| [0018](0018-colour-carries-meaning.md) | Colour carries meaning: no colour per scenario; bars green = good, yellow = slow, red = failed | Accepted |
| [0019](0019-visible-state-and-feedback.md) | Visible selection everywhere; editor gives immediate feedback on changes | Accepted |
| [0020](0020-simple-first-features-one-by-one.md) | Simple first: core flow (set up, run, presets, inspect), then features one by one | Accepted |
| [0021](0021-ui-scales-to-long-runs.md) | UI must scale to runs of hours with many actors, calls and logs | Accepted |
| [0022](0022-core-flow-ui-and-scaling-choices.md) | Core-flow UI choices: placeholder slow rule, bounded data, own VirtualList | Accepted (slow rule Proposed) |
| [0023](0023-dock-magnification-on-bar-charts.md) | macOS-Dock-like magnification on bar charts, only in the scenario popup; hovered bar shows actor number and timestamps | Accepted (update pending) |
| [0024](0024-editor-and-overview-share-the-loaded-plan.md) | Editor and overview show the same view of the loaded scenarios; editing the loaded plan, not a preset; editor uses the overview's cards | Accepted (not implemented) |
| [0025](0025-build-info-and-no-stale-cache.md) | Show build commit and deploy date; avoid stale cached builds | Accepted, implemented |
| [0026](0026-stable-clicking-on-live-charts.md) | Stable clicking on live charts: freeze while aiming, pick on pointer-down, selection sticks to the actor | Accepted (not implemented) |
| [0027](0027-run-player-bar.md) | Run controls as a bottom "player bar" (play/pause/stop + run details), subtle cockpit look | Accepted (not implemented) |
| [0028](0028-interesting-actors-first.md) | Failed and slow actors first: outcome-coloured actor numbers, failed→slow→ok ordering, kept longer, error dictionary | Accepted (not implemented) |
| [0029](0029-every-info-drills-down.md) | UI principle: every shown info (counter, figure, status) is clickable and drills down to the details | Accepted |
| [0030](0030-compact-statistics.md) | Some statistics come back: compact inline, coloured good/bad, no boxes, clickable | Accepted (figures/placement Proposed) |
| [0031](0031-touch-gestures-on-popup-chart.md) | Touch on the popup chart: tap selects (no zoom), sideways swipe magnifies, a finger at the edge pans, pinch and double tap zoom | Accepted, implemented (update 2: stable grid, swipe magnifier, edge pan, double tap; its swipe-on-bars magnifier is superseded by 0037) |
| [0032](0032-group-bars-look-different.md) | Group bars: lighter fill with outline plus a small count badge; single actors stay solid | Accepted, implemented |
| [0033](0033-environments.md) | Environments: arbitrary key-value pairs (credentials allowed), used as `{{Key}}` placeholders and in code, like Bruno; one file per environment; picked in a top-bar dropdown like Bruno | Accepted, implemented (fake data); runner and REST in the server, UI on fake data |
| [0034](0034-scenario-bursts.md) | Spawning pattern per scenario entry: linear (actors/min) or bursts (actors per burst × bursts per minute) | Accepted, implemented (fake data) |
| [0035](0035-zoom-overview-strip.md) | Overview strip under the popup chart: whole run with the viewed area highlighted | Accepted, implemented (update 1: whole width, muted red/amber marks of failed and slow actors, always visible) |
| [0036](0036-step-through-failed-and-slow.md) | Previous/next failed (or slow) moves the zoomed view and steps through groups | Accepted, implemented |
| [0037](0037-popup-chart-selection-swipe-areas-group-stripes.md) | Popup chart: selection arrow below the bar, magnifier only on the axis strip, bars area scrolls, striped groups, even gaps | Accepted, implemented |
| [0038](0038-unfold-bursts-on-zoom.md) | Zooming into a burst unfolds it into single actors side by side; the rest keeps the time axis | Accepted, implemented (cap and failed-first superseded by 0043) |
| [0039](0039-fake-seed-run-30-minutes.md) | The fake data starts with a 30 minute finished run (bursts to zoom into) | Accepted, implemented |
| [0040](0040-popup-chart-robust-navigation.md) | Popup chart navigation bullet proof: a swipe never zooms, bursts unfold as soon as their actors fit (x stays linear in time), redesigned overview strip, persistent grabbable selection pill | Accepted (goal, selection pill), design Proposed, implemented |
| [0041](0041-graded-slow-colour.md) | Slowness as a colour gradient from green (typical time) to amber; the slow line stays for counting and filters | Accepted, implemented (details Proposed) |
| [0042](0042-custom-script-scenarios-and-lifecycle.md) | Custom C# script scenarios (Roslyn upload/compile/test-run), Startup/Execute/Teardown lifecycle hooks, and Fake/Live UI toggle | Accepted, implemented |
| [0043](0043-bursts-split-into-even-groups.md) | A burst that does not fit splits into even groups (refining while zooming); failed/slow stay in their group | Accepted, implemented |
| [0044](0044-selection-marker-one-piece.md) | The lilac selection marker (caret, stem, pill) is one robust piece that always points at the selected bar | Accepted (goal), design Proposed, not implemented |
| [0045](0045-sqlite-storage-and-scalable-logs.md) | SQLite WAL storage (batched channel writer + indexed filters) and scalable lazy-loaded/virtualized logs & actor details | Accepted, implemented (SQLite storage dropped by 0054) |
| [0046](0046-duckdb-storage-and-run-export.md) | DuckDB columnar storage (`DuckDBAppender` bulk ingestion) as default provider + run export (`parquet`, `duckdb`, `json`, `csv`) | Accepted, implemented |
| [0047](0047-ai-assistant-module.md) | AI assistant: server-side Microsoft.Extensions.AI agent in its own module (ports + adapters), env var config (OpenAI-compatible), server-owned plan and presets, right side chat panel, hidden in Fake mode | Accepted (layout Proposed; approval superseded by 0048), implemented (R1 to R3, see 0050, 0051) |
| [0048](0048-assistant-acts-without-approval.md) | The assistant runs every tool directly, deletes and run control included; no approval card | Accepted |
| [0049](0049-assistant-shows-data-in-chat-cards.md) | The assistant shows data in small chat cards (logs box with Show more full screen, bees, bee, scenario/run, plan changes) via show tools; picked bees together on one time line like the popup chart | Accepted (principle, bee timeline), kinds Proposed |
| [0050](0050-plan-workspace-sync.md) | The server owns the Live plan and presets (`PlanWorkspace` + REST); the client pushes edits and pulls changes by polling | Proposed, implemented |
| [0051](0051-assistant-module-layout-and-agent-loop.md) | Assistant module: contracts/Ai/adapters, ports, own tool loop, tools as attributed methods, secret masking, bounded answers | Proposed, implemented |
| [0052](0052-environment-tab-runtime-data-shutdown.md) | Environment tab: inspect static values (the selected environment, fixed during a run) and runtime data (read and written by preflights and scenarios during a run); run bag + entry bags, final values kept; a shutdown program that always runs at the end | Accepted; Environment tab UI draft with fake data done (S1), shutdown program not yet (S2) S2: shutdown program + runtime data in the server runner (implementation notes). |
| [0053](0053-stopped-bees-are-canceled.md) | Bees in flight when a run is stopped get the status canceled and keep their logs and calls; they look and count like failed (supersedes "not reported" in 0004) | Accepted, implemented (step T) |
| [0054](0054-module-split.md) | Split the code into modules: fewer mistakes and easier work for agents, and a runner without the UI (CI) | Accepted (goals), split Proposed; U1 (rename + Contracts), U2 (Engine, Scripting, Storage) and U3 (command-line tool) done |
| [0055](0055-warnings-are-errors.md) | Warnings are errors in every project (`Directory.Build.props`); NuGet audit warnings stay warnings (Proposed) | Accepted, implemented |
