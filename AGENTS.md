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
  - `Services/Scenarios/` — `IScenario` and the built-in scenarios. New scenario = new class + `AddScenario<T>()` in `Program.cs`.
  - `Services/LoadRunner.cs` — open-model scheduler; `Services/LoadPlan.cs` — the plan (also the preset format).
  - `Services/PresetStore.cs` — presets as JSON files (`Presets:Directory`, default `presets/`).
  - `Components/Pages/Home.razor` — the page; `LiveChart.razor`, `ScenarioEntryCard.razor`, `Stat.razor`.
- Interactive pages need `@rendermode InteractiveServer`, otherwise buttons do nothing.

## Build and run
```bash
dotnet run --project src/PerformanceTool
```
If only an older SDK is available (the cloud agent environment has .NET 10), verify by building a **copy**
with the target framework changed to `net10.0`. Don't change the committed `net11.0` target.

## UX bar
The UI must be interactive with a really good UX (brain 0006): every data element can be clicked to inspect
it, used as a filter and isolated. Don't add read-only data views.

## UI changes need a screenshot
Whenever you change the UI, run the app, drive it with Playwright (a real run, not just the empty page) and
**show the user a screenshot**. Run with `ASPNETCORE_ENVIRONMENT=Development` so static assets resolve.
