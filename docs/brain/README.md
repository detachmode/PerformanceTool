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
| [0018](0018-colour-carries-meaning.md) | Colour carries meaning: no colour per scenario; colour only when errors or slow calls occur | Accepted |
| [0019](0019-visible-state-and-feedback.md) | Visible selection everywhere; editor gives immediate feedback on changes | Accepted |
| [0020](0020-simple-first-features-one-by-one.md) | Simple first: core flow (set up, run, presets, inspect), then features one by one | Accepted |
| [0021](0021-ui-scales-to-long-runs.md) | UI must scale to runs of hours with many actors, calls and logs | Accepted |
| [0022](0022-core-flow-ui-and-scaling-choices.md) | Core-flow UI choices: placeholder slow rule, bounded data, own VirtualList | Accepted (slow rule Proposed) |
| [0023](0023-dock-magnification-on-bar-charts.md) | macOS-Dock-like magnification on bar charts to make small bars easy to click | Accepted (not implemented) |
| [0024](0024-editor-and-overview-share-the-loaded-plan.md) | Editor and overview show the same view of the loaded scenarios; editing the loaded plan, not a preset; editor uses the overview's cards | Accepted (not implemented) |
