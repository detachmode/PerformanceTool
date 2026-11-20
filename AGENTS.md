# AGENTS.md

Instructions for coding agents (and humans) working in this repo.

## Read before you change anything
1. **`docs/brain/`** holds the decision records: what we built and why. Read the index in
   `docs/brain/README.md` and the records for the area you're touching **before** making changes.
   Don't contradict a record silently: follow it, or add a new record that supersedes it.
2. **`docs/plan.md`** is the backlog. Check it before starting new features, and update it when you finish one.

## Record your decisions
When you make a decision someone might later ask "why?" about (architecture, dependency, data format,
trade-off, rejected alternative), add a record to `docs/brain/` using the template in its README and add it
to the index. Commit it in the same change as the code.

## Project
- `src/PerformanceTool` — Blazor Web App, interactive **server** rendering, `net11.0`.
  - `Services/Scenarios/`: `Scenario<TParameters>` and the built-in scenarios. A new scenario is a parameter class
    plus a scenario class plus `AddScenario<T>()` in `Program.cs`. The editor is **generated** from the parameter
    class (`Services/Scenarios/Spec/ParameterSpec.cs`, `Components/Parameters/`), never hand-written per scenario (brain 0007).
  - `Services/LoadRunner.cs` — open-model scheduler; `Services/LoadPlan.cs` — the plan (also the preset format).
  - `Services/PresetStore.cs` — presets as JSON files (`Presets:Directory`, default `presets/`).
  - `Components/Pages/Home.razor` — the page; `LiveChart.razor`, `ScenarioEntryCard.razor`, `Stat.razor`.
  - `Components/ScenarioTile.razor` (live tile per entry) and `ScenarioDetailDialog.razor` (click a tile); `Services/Scenarios/Spec/ParameterSummary.cs` (read-only parameter view). `LoadRunner` reports `ExecutionActivity` (start/end, in-flight) for them (brain 0008).
  - Styling: dark Aspire-like tokens at the top of `wwwroot/app.css` (brain 0008); keep it flat, dark only.
- Interactive pages need `@rendermode InteractiveServer`, otherwise buttons do nothing.

## Build, test and run
```bash
dotnet test
```
```bash
dotnet run --project src/PerformanceTool
```
If only an older SDK is available (the cloud agent environment has .NET 10), copy `src/`, `tests/` and the
`.slnx` to a scratch folder, change every `net11.0` to `net10.0` there, and build and test the copy. Don't change
the committed `net11.0` targets.

## UX bar
The UI must be interactive with a really good UX (brain 0006): every data element can be clicked to inspect
it, used as a filter and isolated. Don't add read-only data views.

## UI changes need a screenshot
Whenever you change the UI, run the app, drive it with Playwright (a real run, not just the empty page) and
**show the user a screenshot**. Run with `ASPNETCORE_ENVIRONMENT=Development` so static assets resolve.
