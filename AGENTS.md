# AGENTS.md

Instructions for coding agents (and humans) working in this repo.

## Read before you change anything
1. **`docs/brain/`** holds the decision records: what we built and why. Read the index in
   `docs/brain/README.md` and the records for the area you're touching **before** making changes.
   Don't contradict a record silently: follow it, or add a new record that supersedes it.
2. **`docs/context.md`** is the domain language. Use its terms (execution, scenario entry, run, …) consistently in
   code, UI and docs.
3. **`docs/plan.md`** is the backlog. Check it before starting new features, and update it when you finish one.

## Record your decisions
When you make a decision someone might later ask "why?" about (architecture, dependency, data format,
trade-off, rejected alternative), add a record to `docs/brain/` using the template in its README and add it
to the index. Commit it in the same change as the code.

## Project
Projects (brain 0015, 0054), all `net11.0`, solution `Beesharp.slnx`; namespaces are `Beesharp.*`. Old user scripts with `using PerformanceTool.*` still compile (`LegacyScriptNamespaces`):
- `src/Beesharp.Core` — class library shared by client and server. Keep it free of ASP.NET and browser dependencies.
  - `Services/Scenarios/`: `Scenario<TParameters>` and the built-in scenarios. A new scenario is a parameter class
    plus a scenario class plus `AddScenario<T>()` in **both** `Program.cs` files (client and server). The editor is **generated** from the
    parameter class (`Services/Scenarios/Spec/ParameterSpec.cs`, client `Components/Parameters/`), never hand-written per scenario (brain 0007).
  - `Services/Runtime/RunData.cs`: runtime data (brain 0052): the **run bag** (`ScenarioContext.RunBag`, `Data` in preflight/shutdown scripts) and one **entry bag** per entry (`ScenarioContext.State`), with last writer and run time per key; `DataWriter`/`RuntimeValue`/`RuntimeDataSnapshot` are also the REST wire format.
  - Stopped runs (brain 0053): `LoadRunner` reports bees in flight at the stop as canceled (`ExecutionResult.Canceled`, error `ExecutionResult.CanceledError`); they look and count like failed.
  - `Services/LoadPlan.cs` (the plan, also the preset format), `ExecutionModels.cs` (`ExecutionResult`, `ExecutionActivity`).
- `src/Beesharp.Contracts` — the **REST wire types** shared by server and client (run status, stored runs, bee detail, log pages, scripts, preflights, shutdowns, plan, presets, request bodies). **A REST type goes in Contracts, never duplicated** on the server or the client: add the record there, use it in the endpoint and in the client call. Property names are the JSON (web defaults); `ContractsRoundTripTests` guards them. It references only Core (`LoadPlan`, `RecordedCall`/`RecordedLog` are the shared model); the assistant's wire types stay in `Beesharp.Ai.Contracts`. Server and client csprojs import `Beesharp.Contracts` as a global using.
- `src/Beesharp.Client` — the UI, standalone **Blazor WebAssembly**. Pages `/` (overview), `/editor` and `/environment` (static values of the selected environment plus runtime data: run bag and entry bags, `RunSnapshot.Runtime`, fake data in `Services/RuntimeData.cs`, brain 0052).
  - `Services/IRunSource.cs` is the seam to the data: `FakeRunSource` (in-browser simulation, the default) and `ApiRunSource` (stub, throws until the REST API exists).
    Choose with `"DataSource": "Fake" | "Api"` in `wwwroot/appsettings.json`.
  - `Pages/`, `Layout/` (top bar and the bottom `PlayerBar` with play/pause, stop, run readouts and Logs, brain 0027), `Components/` (`ScenarioCard`, popups `ScenarioDetailDialog` and `LogsDialog`, `VirtualList`, `ActorBars`, `Parameters/`), `wwwroot/app.css` (dark tokens; flat, dark only; colour only means failed = red or slow = amber, brain 0018).
  - **Slow rule (placeholder):** `Services/SlowCallRule.cs` is the one place that defines "slow" and the graded colour from typical time to amber (`Ratio`, `Step`, brain 0041; an actor keeps its worst call's ratio in `ActorRecord.RatioCode`; CSS classes `t1..t3` shift the green). **Scale (brain 0021/0022):** `RunStore` keeps tiny actor records for the whole run and calls/logs for at most `RunStoreOptions.MaxDetailedActorsPerScenario` (1500) actors per scenario, dropping successful actors first, then slow, then failed (brain 0028); each distinct error text is stored once in `ErrorDictionary` (`store.Errors`; `ActorRecord`/`FakeCall`/`FakeLogEntry` hold ids, `Error`/`Message`/`Exception` resolve them; `store.HasDetail(seq)` tells whether detail is kept); long lists use `VirtualList`, charts are bounded (`ActorBars`). The popup chart's zoomed view unfolds bursts into single-actor bars as soon as they fit (`ActorBars.Unfold` with `UnfoldRoom`/`UnfoldHysteresis`; the burst borrows quiet time around it, so x stays linear in time; brain 0038, 0040) and its overview strip shows muted failed/slow marks (`ActorBars.OverviewMarks`, brain 0035) with an outlined view window (brain 0040). Gesture rules (a sideways swipe never zooms), the strip and the selection pill live in `wwwroot/dock-magnify.js` (brain 0040). The seeded finished run is 30 minutes (brain 0039). Debug: open the app with `?simulate=3h` to pre-generate a 3 hour run, `?burst=100` to make the seeded bursts that big. A burst that does not fit as single bars splits into even groups (`ActorBars.NiceGroupSize`, `ActorBar.GroupLo/GroupHi`, brain 0043).
  - **Trimming:** Release publish trims. `Beesharp.Core` is a `TrimmerRootAssembly` in the csproj so the reflection-based editor keeps its metadata. Always test the *published* build, not only `dotnet run`.
- `src/Beesharp.Storage` — `IRunStorage` + `InMemoryRunStorage` (brain 0045/0046), `StorageOptions` (provider choice; only `DuckDb` and `Memory`, SQLite was removed and `"Storage:Provider": "Sqlite"` fails at startup naming the supported ones), the JSON file stores `PresetStore` and `EnvironmentStore` and `BeesharpDirectories` (the presets, environments and scripts folders). No ASP.NET, no database engine.
- `src/Beesharp.Storage.DuckDb` — `DuckDbRunStorage` (the default; `DuckDBAppender` ingestion, exports parquet/duckdb/json/csv, runtime data table; file `data/beesharp.duckdb`, `Storage:DuckDbPath`) and `AddBeesharpStorage(StorageOptions)`. Own project so a consumer of only the in-memory provider avoids the native DuckDB binaries.
- `src/Beesharp.Scripting` — the one Roslyn compile-and-store implementation: `ScriptCompiler` (common references/imports, debug info for stack traces, `LegacyScriptNamespaces` upgrade, diagnostics), `ScriptNames` (ids, file names), `ScriptFolder` (files), then `ScriptScenarioStore` (scenario scripts, test run) and `ScriptProgramStore` with `PreflightStore` (`scripts/preflight/`) and `ShutdownStore` (`scripts/shutdown/`). A new kind of script adds only its globals, references and imports. No ASP.NET. `AddBeesharpScripting()`.
- `src/Beesharp.Engine` — the runner, usable without ASP.NET (a console program can use it): `LoadRunner` (open-model scheduler), `RunManager` (run lifecycle: preflight, scenarios, shutdown program, live state and logs, runtime data, `GET .../runtime` data), `PlanWorkspace` (the Live plan, brain 0050). `AddBeesharpEngine(BeesharpDirectories)` registers the engine, the file stores and the script stores; add storage with `AddBeesharpStorage`; call `LoadBeesharpScriptsAsync()` once after building the container.
- `src/Beesharp` — ASP.NET Core host, **wiring only** (brain 0054): `Program.cs` composes the modules (Engine, Scripting, Storage, Ai) and maps endpoints; every REST endpoint lives in `Api/*Endpoints.cs` by area (`Scenario`, `Script`, `Program` (preflights and shutdowns share one mapping), `Run`, `RunQuery`, `Plan`, `Environment`). Settings `Presets:Directory`, `Environments:Directory`, `Scripts:Directory`, `Storage:*` are read in `Program.cs` only. The AI adapters (`Ai/`) stay here. A new endpoint goes in the file of its area, never inline in `Program.cs`.
  The **shutdown program** (`LoadPlan.ShutdownScriptId`, `/api/shutdowns`) always runs at the end of a run (also after Stop or a failed preflight). Runtime data: `GET /api/runs/current/runtime`, `/api/runs/{id}/runtime`; final values are stored per run (`IRunStorage.SaveRuntimeDataAsync`).
- `src/Beesharp.Ai.Contracts`, `src/Beesharp.Ai` — the **AI assistant** (brain 0047 to 0051). Contracts = wire DTOs (no dependencies; the client references only these, never Microsoft.Extensions.AI).
  `Beesharp.Ai` = the agent on Microsoft.Extensions.AI: `AddBeesharpAi(config)`, `MapBeesharpAi()` (`/api/assistant`: `GET status`, `POST chat`, `DELETE sessions/{id}`), `AssistantService` (own tool loop, sessions in memory),
  `Ports/` (the interfaces it needs: plan, run, run history, environments, scripts; it never references the host) and `Tools/AssistantToolbox*.cs`. The host implements the ports in `src/Beesharp/Ai/*Adapter.cs`.
  **Configure** with environment variables (section `AI`): `AI__Model` (required), `AI__ApiKey` and/or `AI__Endpoint` (any OpenAI-compatible server, e.g. Ollama `http://localhost:11434/v1`), `AI__Provider` (only `OpenAI`),
  `AI__SystemPrompt`, `AI__MaxToolCalls` (default 24). Without a model the assistant is "not configured" and the chat icon stays hidden.
  **Add a tool:** a public method with `[AssistantTool("snake_name")]` and `[Description]`s (tool language, entries and bees numbered from 1) in the toolbox file of its area; bound its output; call `rec.Read(...)`, `rec.Change(area, ...)` or `rec.Show(card)` with a one-line summary. No approval step (brain 0048). A new card kind: a `ViewKinds` constant, a builder in `AssistantToolbox.Show.cs`, a card in `Components/Assistant/`.
  The server owns the Live plan: `Services/PlanWorkspace.cs` + `Api/PlanEndpoints.cs` (`/api/plan`, `/api/presets`, brain 0050); `ApiRunSource` pushes editor edits and pulls server changes (400 ms loop).
  The chat panel is `Components/Assistant/` in the client (right side panel, Live mode only, only when `GET /api/assistant/status` says configured).
- `tests/Beesharp.Tests` — references all projects (one test project per project comes in U4).

## Build, test and run
```bash
dotnet test
```
Client alone (fake data): `dotnet run --project src/Beesharp.Client`. Server plus client:
```bash
dotnet run --project src/Beesharp
```
**Warnings are errors** in every project (`Directory.Build.props`, brain 0055): build without warnings. Copy `Directory.Build.props` into the scratch copy too. .NET 11 (CI) reports more than the .NET 10 copy (e.g. Blazor BL0016: give every JS interop call its own try/catch; BL0012: no `StateHasChanged()` at the end of an event handler), so watch CI.
CI: `.github/workflows/ci.yml` runs restore, build and test (Release) on pushes to `main` and on PRs (brain 0014). Keep `dotnet test` green.
If only an older SDK is available (the cloud agent environment has .NET 10), copy `src/`, `tests/`, the
`.slnx` and `Directory.Build.props` to a scratch folder, change every `net11.0` to `net10.0` there, and build and test the copy. Don't change
the committed `net11.0` targets (also change `Microsoft.AspNetCore.Components.WebAssembly` `11.0.*-*` to `10.0.*` in the copy).

**GitHub Pages:** `.github/workflows/pages.yml` publishes the client (Release, trimmed) on every push to `main` and on manual dispatch, rewrites
`<base href>` to `/PerformanceTool/`, copies `index.html` to `404.html` (deep links like `/editor`) and adds `.nojekyll`. Site: https://detachmode.github.io/PerformanceTool/
(fake data only). The repo setting Pages > Source must be "GitHub Actions". To test locally, publish, apply the same rewrite and serve under a `/PerformanceTool/` path.
**Build info and cache (brain 0025):** the workflow publishes with `-p:BuildSha=${{ github.sha }} -p:BuildTime=<UTC>` (shown by `Layout/BuildStamp.razor`),
writes `build-info.json` (the page fetches it to offer "New version available, reload"), and adds `?v=<sha>` to `app.css` and `favicon.svg`. Blazor fingerprints `_framework/*` and the
JS modules via the importmap in `index.html`. Pages allows no custom HTTP headers. When you add a stylesheet or icon to `index.html`, add it to the `?v=` sed in `pages.yml` too.

## UX bar
The UI must be interactive with a really good UX (brain 0006): every data element can be clicked to inspect
it, used as a filter and isolated. Don't add read-only data views. **Every counter or figure drills down** to the
details behind it (brain 0029), e.g. an errors counter opens the failed actors.

## Screenshots in the repo and in PRs
- Never overwrite or rename existing files in `docs/screenshots/`; earlier PR descriptions link to them.
- Put each set of screenshots in a **new, uniquely named folder**, e.g. `docs/screenshots/2026-11-20-dock-magnify/`.
- Each screenshot folder has a `README.md` gallery that embeds its images (relative paths). GitHub renders it with the
  images inline.
- **The GitHub tool used by agents defuses image embeds *and* markdown links to github.com / raw.githubusercontent.com in
  PR descriptions** (`![..](..)`, `[..](..)` and `<img>` get wrapped in code spans). So PR descriptions give the
  folder's README gallery as a **bare URL on its own line** (GitHub auto-links it), commit-pinned. The tool also wraps
  URLs longer than ~156 characters (also inside image embeds). So each PR gets **one inline highlight image** with a
  **short URL** (7-character commit SHA, total ≤ ~150 chars):
  `![What it shows](https://raw.githubusercontent.com/<owner>/<repo>/<sha7>/docs/screenshots/<folder>/<file>.png)`
  plus the gallery as a bare URL:
  `https://github.com/<owner>/<repo>/blob/<commit-sha>/docs/screenshots/<folder>/README.md`.
- If a raw image URL is needed elsewhere, use a **commit-pinned raw URL**:
  `https://raw.githubusercontent.com/<owner>/<repo>/<commit-sha>/docs/screenshots/...png`, not a branch name.
  Don't use `github.com/.../blob/...?raw=true`: it redirects, and many viewers (apps, notifications) then show only the
  URL instead of the image.

## UI changes need a screenshot
Whenever you change the UI, run the app, drive it with Playwright (a real run, not just the empty page) and
**show the user a screenshot**. Run with `ASPNETCORE_ENVIRONMENT=Development` so static assets resolve.
