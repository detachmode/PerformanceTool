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
| [0001](0001-blazor-server-on-dotnet-11.md) | Blazor Server (interactive server rendering) on .NET 11 | Accepted |
| [0002](0002-live-bar-chart-in-html-css.md) | Live bar chart in plain HTML/CSS, one bar per execution, coloured per scenario | Accepted |
| [0003](0003-code-defined-scenarios.md) | Scenarios are defined in C# code, configured in the UI | Accepted |
| [0004](0004-open-model-rate-scheduling.md) | Open-model scheduling: runs per minute per scenario, drop when saturated | Accepted |
| [0005](0005-presets-as-json-files.md) | Presets stored as one JSON file each | Accepted |
| [0006](0006-ux-everything-clickable-and-inspectable.md) | UX principle: everything clickable, inspectable, filterable, isolatable | Accepted |
| [0007](0007-scenario-editor-derived-from-typed-spec.md) | Scenario editor UI generated from a typed parameter spec (enum → dropdown, bool → toggle, …) | Accepted, implemented |
| [0008](0008-live-scenario-tiles-overview.md) | Live scenario tiles with mini charts, activity highlighting, detail popup; Aspire-inspired styling | Accepted, implemented |
| [0009](0009-agent-access-to-ci-runs.md) | Agents inspect CI runs: run bundles, artifact + central server (API key), one CLI/API (+ MCP) | Proposed, partly decided in 0010 |
| [0010](0010-azure-devops-pipeline-artifacts.md) | Azure DevOps: tool runs in the pipeline; results as a full artifact + a small summary artifact | Accepted (not implemented) |
| [0011](0011-overview-and-editor-pages.md) | Separate lightweight overview page (tiles with Aspire-style box charts + details popup) and editor page | Accepted (not implemented) |
| [0012](0012-scenario-logging.md) | Scenarios log via ILogger with one scope per actor (one bar); logs persisted (CI: full artifact) and shown when inspecting a scenario | Accepted (not implemented) |
| [0013](0013-call-durations-are-first-class.md) | Call durations inside each actor are a first-class result; recorded via an HttpClient DelegatingHandler; shown as stacked actor bars (up to ~50 calls) | Accepted (not implemented) |
| [0014](0014-ci-on-github-actions.md) | GitHub Actions CI: restore, build, test (TRX artifact + summary) on every push/PR | Accepted |
