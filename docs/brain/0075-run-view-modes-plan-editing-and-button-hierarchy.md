# 0075. Run view modes, load-only plan editing and unified button hierarchy

- Status: Accepted, implemented
- Date: 2026-11-28

## What

1. **Explicit workspace view mode (`RunViewMode`) and title on `RunSnapshot`** (`src/Beesharp.Client/Runs/RunModels.cs`, `Data/ApiRunSource.cs`, `Data/FakeRunSource.cs`):
   Every `RunSnapshot` carries one of four mutually exclusive modes and its own `Title` (`DisplayTitle`):
   - `IdlePlan` — configuring the editable draft plan; no run data in any view (`Overview`, `Calls`, `Logs`, `Environment`, `PlayerBar`); `CanEditPlan = true`; `Title` is the loaded preset name or `"New Plan"`.
   - `LiveRun` — inspecting an active (running or paused) run; `IsPlanReadOnly = true` (cards show the eye-icon parameter inspector instead of the edit pencil, launch bar switches to `Stop run`); `Title` is the run's plan title.
   - `FinishedRun` — inspecting the just-finished live run; `CanEditPlan = true`; any plan edit immediately detaches to `IdlePlan`.
   - `HistoricalRun` — inspecting a stored run loaded from History; `IsPlanReadOnly = true`; `Title` is resolved from the stored run's plan (matching preset name or timestamped `"Run · dd MMM, HH:mm:ss"`); the draft `Plan` and `PresetName` stay untouched and are restored by `Back to live`.
2. **Load-only presets and `"New Plan"` on edit** (`Overview.razor`, `PlanWorkspace.SetPlan`, `IRunSource.MarkPlanEdited`):
   - Presets are loaded into the working plan (`Load preset ▾`); there is no in-place save/overwrite preset flow in the Overview header.
   - Any edit to the plan (duration, limits, lifecycle scripts, scenarios, parameters) or clicking `New Plan` in the `•••` menu calls `MarkPlanEdited()`: sets the title to `"New Plan"` (synced to `PlanWorkspace`), clears the active run snapshot across all views (`dismissedRunId` prevents the idle sync loop from re-pulling the dismissed finished run while still picking up brand-new runs started by other clients).
   - Opening/reloading `/` always lands in Overview (`IdlePlan`/`LiveRun`/`FinishedRun`), never stuck in `?edit=1`.
3. **Unified button hierarchy** (`src/Beesharp.Client/wwwroot/app.css`):
   - **Primary** (`.btn.primary`, `.btn.start`, `Start run`): emerald green (`--btn-primary-bg: #059669`, hover `#10b981`, white text) for main go/commit actions (`Start run`, `Done`, `Save`, `+ Add scenario`, `Send`).
   - **Secondary** (`.btn.secondary`): light/white (`--btn-secondary-bg: #e3e4ec`, hover `#ffffff`, dark text) for prominent secondary actions (`Edit`, `▶ Try`, `Undo`, `Edit plan`, `Use this environment`).
   - **Tertiary / Ghost** (`.btn.tertiary`, `.btn.ghost`, default `.btn`): dark grey (`--btn-tertiary-bg: #2e333c`) for supporting utility actions (`Logs`, `Export`, `Plan settings`, `Configure…`, `•••`).
   - **Danger / Stop** (`.btn.danger`, `.btn.stop`, `Stop run`): coral red tint (`--btn-danger-bg`) for destructive and stop actions.

## Why

- Earlier, pages inferred state from several separate mutable flags (`ApiRun.IsViewingHistory`, `Sim.IsRunning`, `Sim.PresetName`, `ApiRun.CurrentRunId`). That caused mismatches when transitioning between editing a preset, inspecting a finished run, starting a new plan, and loading a historical run (e.g. a historical run showing `"New Plan"` in the title, or stale `Calls`/`Logs` remaining visible after starting a new plan).
- Making `RunViewMode` and `Title` first-class immutable properties on `RunSnapshot` gives `Overview`, `PlayerBar`, `Calls`, `Logs`, and `Scenario` one source of truth.
- Separating primary (green), secondary (white), tertiary (dark grey), and danger (red) buttons via CSS tokens eliminates ad-hoc per-component colours.

## Consequences

- Every UI surface should read `s.ViewMode` (`s.IsHistorical`, `s.IsLiveRun`, `s.CanEditPlan`, `s.IsPlanReadOnly`) and `s.DisplayTitle` from `RunSnapshot` instead of checking `ApiRun.IsViewingHistory` or `Sim.PresetName` directly.
- Transitions are covered by `tests/Beesharp.Client.Tests/RunViewModeTests.cs`.
- Future work: persisting the preset name on stored runs in DuckDB would avoid fingerprint matching for historical runs whose preset was later modified or deleted.
