# 0054. Split the code into modules with clear boundaries

- Status: Accepted (goals), split Proposed
- Date: 2026-11-22

## What (user decision)
Restructure the projects, after step T (0053), with two goals:
1. **Fewer mistakes and easier work for agents**: clear boundaries, less duplication.
2. **A runner without the UI**, e.g. in CI pipelines.

## Analysis of the code before the split (Claude, 2026-11-22)
- Good: the AI module (contracts / module / ports, host adapters) is the pattern to follow; Core is free of
  ASP.NET, Roslyn and databases; `IRunSource` cleanly separates Fake and Live; the editor is generated from `ParameterSpec`.
- Weak:
  - The run API's wire types are written twice by hand (public records in `RunManager.cs`, private copies in the
    client's `ApiRunSource.cs`); a change on one side breaks the other at runtime, not at build time.
  - The host holds the runner (`LoadRunner`, `RunManager`), Roslyn compiling, three storage engines and the REST
    API; the runner can't run without ASP.NET, and DuckDB, SQLite and Roslyn are tied to the web host. 24 of 40
    endpoints are inline in `Program.cs`.
  - Roslyn compiling and the script store exist twice (`ScriptScenarioStore`, `PreflightStore`; `ShutdownStore`
    derives from the preflight one).
  - Core mixes what a scenario author writes against (`IScenario`, `ScenarioContext`, attributes) with the tool's
    model (`LoadPlan`, `ParameterSpec`, catalog); everything sits under `Services/`.
  - The client sorts by kind (`Services/` mixes data sources, result data and view state); some very large files
    (`ScenarioDetailDialog.razor` 1100 lines, `dock-magnify.js` 1100, `Overview.razor` 800).
  - Project names say `Beesharp`, namespaces and AGENTS.md still say `PerformanceTool`.
  - One test project references everything.

## Proposed split (Claude, not decided)
```
Beesharp.Scenarios       what scenario code compiles against: IScenario, ScenarioContext, attributes,
                         environment values, run bag / entry bag
Beesharp.Core            the tool's model: LoadPlan, ParameterSpec, Placeholders, catalog
Beesharp.Contracts       REST wire types shared by server and client (like Ai.Contracts)
Beesharp.Engine          LoadRunner, RunManager, preflight → scenarios → shutdown; no ASP.NET
Beesharp.Scripting       one Roslyn compiler and store for scripts, preflights, shutdown programs
Beesharp.Storage         IRunStorage + in-memory; DuckDb provider beside it (SQLite dropped)
Beesharp.Ai(.Contracts)  unchanged
Beesharp (host)          wiring only; endpoints in Api/*; AI adapters
Beesharp.Cli             runs a preset headless (CI)
Beesharp.Client          folders by topic: Runs/, Chart/, Editor/, Environment/, Logs/, Assistant/, Data/
tests/                   one test project per project
```

## Answers (user, 2026-11-22)
- **CI runner = a command-line tool**, a separate small program (no web server), e.g.
  `beesharp run --preset "Black Friday" --env Staging`: runs the preset, prints a summary, saves the run, and exits
  with an error code when bees failed.
- **Exit code: any failed bee fails the pipeline** (canceled bees count as failed, 0053). Also an error code when
  the run can't run (e.g. the preflight failed).
- **Rename to Beesharp**: all namespaces become `Beesharp.*`; AGENTS.md and the docs describe the real folders.
  Existing user scripts with `using PerformanceTool...` keep working (compatibility) or need a one-line change.
- **Drop SQLite storage**: keep DuckDB (default) and in-memory (tests, quick tries). Partly supersedes 0045
  (SQLite storage) and the "Sqlite remains available" note in 0046.

- **Steps** (user agreed the order), each with green tests and a screenshot check:
  - **U1** rename to Beesharp + shared `Beesharp.Contracts` (removes the duplicated wire types).
  - **U2** `Engine`, `Scripting` and `Storage` out of the host; SQLite dropped.
  - **U3** the command-line tool.
  - **U4** client folders by topic; one test project per project.

## Open
- Whether `Beesharp.Scenarios` (what scenario code compiles against) is split from Core: Proposed, decide in U2.

## Implementation notes (U1) (Claude, Proposed)
- **Rename**: every namespace, `RootNamespace` and type name `PerformanceTool*` became `Beesharp*` (src and tests). Kept on purpose: the GitHub Pages base path `/PerformanceTool/` and repository URLs (they are the repo's name), the data file names `perftool.duckdb` / `perftool.db` and the browser storage key `perftool.fakeMode` (renaming would lose users' runs and the mode setting), old screenshots and the history in brain records. The `User-Agent` of the runner's HTTP client changed from `PerformanceTool/1.0` to `Beesharp/1.0`.
- **Old user scripts**: `LegacyScriptNamespaces.Upgrade` (Core) rewrites `PerformanceTool.Services|Ai|Client|Api` to `Beesharp.` in the script text before Roslyn compiles scenario scripts, preflights and shutdown programs. The replacement stays on its line, so diagnostics keep their line numbers; the stored file is not touched. Checked live: an old script with `using PerformanceTool.Services.Scenarios;` compiles. The sample scripts had no namespace usings, so nothing to update there. The exception formatter and stack trace filter now match `Beesharp`.
- **`Beesharp.Contracts`** holds the REST wire types: `RunContracts.cs` (`LiveRunStatusDto`, `LiveBeeDto`, `LiveScenarioViewDto`, `LiveRunningBeeDto`, `LiveLogEntryDto`, `StoredRunSummary`, `StoredBeeDetail`, `StoredLogRow`, `LogPageResult`, `StartRunRequest`), `ScriptContracts.cs` (`ScenarioInfoDto`, `ScriptScenarioSummary`, `ScriptCompileResult`, `ScriptDiagnosticDto`, `ScriptRecordedCall`, `ScriptTestRunResult`, `PreflightScriptSummary`, `PreflightCompileResult`, `PreflightRunResult`, upload and test-run requests; shutdown programs reuse the preflight types) and `PlanContracts.cs` (`PlanWorkspaceState`, `Preset`, `AddEntryRequest`). The names are the former server names (so the server diff stays small); the client copies (`ServerLogRowDto`, `ClientScriptSummary`, `PlanStateDto`, ...) are gone. `GET /api/scenarios` returns `ScenarioInfoDto` instead of an anonymous object. The JSON is unchanged (same property names). Environments (`EnvironmentDefinition`), `LoadPlan`, `RuntimeDataSnapshot` and `RecordedCall`/`RecordedLog` were already shared in Core and stay there.
- **Contracts references Core** (not dependency-free like `Ai.Contracts`): `LoadPlan` and the recorded calls are part of the wire format and copying them would recreate the duplication. If U2 splits `Beesharp.Scenarios` out of Core, Contracts should reference the part that holds the plan.
- Server and client csprojs add `Beesharp.Contracts` as a global `Using`, so files need no extra using line.
- Left as is: the error bodies `{ error = "..." }` of the endpoints are still anonymous objects (client reads them as `JsonNode`); the assistant's wire types stay in `Beesharp.Ai.Contracts`; `IRunStorage` keeps `LogQuery`, `StoredRunData`, `StoredBeeSummary`, `RunExportResult` (server only).
- A test (`ContractsRoundTripTests`) round-trips the main DTOs through System.Text.Json with the web defaults and checks the JSON property names.

## Implementation notes (U2) (Claude, Proposed)
- **New projects** (graph: `Contracts -> Core`; `Storage -> Core, Contracts`; `Storage.DuckDb -> Storage`; `Scripting -> Core, Contracts, Storage`; `Engine -> Core, Contracts, Storage, Scripting`; host `Beesharp -> Engine, Scripting, Storage, Storage.DuckDb, Ai, Client`). None of Storage, Storage.DuckDb, Scripting, Engine references ASP.NET; the host no longer references Roslyn or DuckDB packages. They keep the namespaces `Beesharp.Services` and `Beesharp.Services.Storage` (files moved with `git mv`; script imports such as `Beesharp.Services.Scenarios` and tests stay valid).
- **DuckDB in its own project** (`Beesharp.Storage.DuckDb`), so a consumer that needs only the in-memory provider avoids the native binaries. `AddBeesharpStorage(StorageOptions)` lives there because it knows both providers.
- **SQLite removed**: provider, tests and `Microsoft.Data.Sqlite` (the NU1903 audit warning is gone). `"Storage:Provider": "Sqlite"` (or any unknown name) throws at startup: `Storage provider 'Sqlite' is not supported. Supported providers: DuckDb, Memory. ...` (`StorageProviderTests`). DuckDB file name and path unchanged (`data/beesharp.duckdb`, `Storage:DuckDbPath`). The tracked dev file `src/Beesharp/data/perftool.db*` was untracked; the folder is gitignored.
- **No `IConfiguration`/`IWebHostEnvironment` in the modules**: options classes (`BeesharpDirectories`, `StorageOptions`) are filled by `Program.cs` from the same config keys as before (`Presets:Directory`, `Environments:Directory`, `Scripts:Directory`, `Storage:*`, relative to the content root). The stores take plain paths (`new PresetStore(dir)`).
- **`PresetStore` and `EnvironmentStore` live in `Beesharp.Storage`** (`Files/`), not in Engine: the script stores need the environment store for test runs and Engine references Scripting. The CLI (U3) gets presets and environments through `AddBeesharpEngine`. **`PlanWorkspace` is in Engine** (no ASP.NET; the CLI will not need it, the host and the assistant do).
- **Scripting unified**: one `ScriptCompiler` (common references and imports, `LegacyScriptNamespaces.Upgrade`, debug info and file name for stack traces, error/warning mapping), `ScriptNames` (id sanitizing, file name rules), `ScriptFolder` (read/save/delete, legacy `.csx` for scenario scripts). Each kind adds only its globals, extra references and imports (scenario: DataAnnotations, Scenarios, Spec; programs: Process, `System.Diagnostics`, `System.IO`, `Beesharp.Services.Runtime`). `ShutdownStore : PreflightStore` became `PreflightStore` and `ShutdownStore` as sibling kinds of `ScriptProgramStore`, and the two endpoint sets are one generic mapping. Visible differences: scripts compiled without a `.cs` extension now show `.cs` in stack traces too; a generated random id is always 18 characters.
- **Host**: all inline endpoints moved from `Program.cs` to `Api/` (`Scenario`, `Script`, `Program`, `Run`, `Environment`; `Plan` and `RunQuery` existed). Routes, JSON and status codes unchanged.
- **Left for later**: `Beesharp.Scenarios` is not split from Core (undecided; it would also change what Contracts references). Client folders and one test project per project are U4. The test project still references everything.

## Implementation notes (U3) (Claude, Proposed)
- **`src/Beesharp.Cli`**: console app referencing Engine, Scripting, Storage, Storage.DuckDb (plus `Microsoft.Extensions.DependencyInjection` for the container implementation); not the host, ASP.NET, client or AI. `beesharp run --preset <name|file> [--env] [--root] [--storage duckdb|memory] [--duration] [--json] [--progress]`, usage in `docs/cli.md`. It drives `RunManager` (no second runner): start, poll counters, summary from the stored run.
- **Assembly is `Beesharp.Cli`, not `beesharp`**: a `beesharp.dll` clashes with the host's `Beesharp.dll` (case-insensitive file systems, and the compiler already confused them). The command is `beesharp` through `PackAsTool` / `ToolCommandName`; from source: `dotnet run --project src/Beesharp.Cli -- run ...`.
- **Hand-written argument parser** (`CliArguments`): one command and a few options; no parser library, no prerelease dependency.
- **Exit codes**: 0 all bees succeeded; 1 a bee failed or was canceled, the run was stopped with Ctrl+C (even with nothing in flight), or the shutdown program failed; 2 could not run (arguments, preset/environment/root missing, a needed script does not compile, unknown scenario/preflight/shutdown, unknown placeholder, preflight failed). Checked before anything starts: plan validation, scripts, placeholders. Bees dropped because too many were in flight are reported but do not fail the run. A run in which no bee finished exits 0 and says so.
- **Broken scripts**: a script that does not compile is a warning on stderr when the preset does not need it, and exit 2 (naming the file and the compile error) when the preset's scenario, preflight or shutdown program is missing because of it.
- **Summary** (`RunSummary`, also the `--json` shape): per entry finished, failed (includes canceled), slow, typical (median) and slowest 5% (nearest-rank) of the bees that did not fail; the 5 most common error texts with counts; preflight and shutdown program outcome; run id and where it was saved. Built from the stored run (the live list is in completion order, not sequence order).
- **Changes outside the CLI**: `ScriptScenarioStore/ScriptProgramStore.LoadAllFromDiskAsync` and `LoadBeesharpScriptsAsync` return the files that did not compile (`ScriptLoadProblem`; the host ignores the result); `RunManager.WaitUntilFinishedAsync()` (run done, shutdown program done, writes flushed). The server's behaviour is unchanged.
- **Open question for the user**: a failing shutdown program exits 1 now (leftovers of a load test matter to a pipeline); it could also stay a warning only, as in the UI.

## Implementation notes (U4) (Claude, Proposed)
- **Client folders by topic** (files moved with `git mv`; only namespaces, usings and one JS path changed): `Data/` (sources, API clients, mode state), `Runs/` (run store, models, slow rule, stats, fake runtime data, `BeeStats`, `BeeOrder`), `Overview/`, `Chart/` (`BeeBars`, `DockZoom`, `BeeChip`, `BeeSideList`, `ScenarioDetailDialog`), `Editor/` (+ `Parameters/`), `Environments/`, `Logs/`, `Assistant/`, `Layout/`, `Shared/`. The old `Services/` and `Components/` folders are gone. Namespaces follow folders (`Beesharp.Client.Chart`, ...); `_Imports.razor` imports all of them, `.cs` files import what they use.
- **Pages stay in `Pages/`**: a page class named like its topic (`Overview`, `Editor`) inside a namespace of the same name is legal but confusing, and one `Pages/` folder shows all routes at a glance. Blazor routes are unaffected.
- **`Environments/`, not `Environment/`**: a namespace `Beesharp.Client.Environment` would shadow `System.Environment` inside the client.
- **Small splits**: `DataModeState.cs` held three classes (`CustomScriptsPopupState` went to `Editor/`, `ScriptApiClient` to `Data/`) and `IEnvironmentSource.cs` held the popup state and display rules (now in `Environments/`).
- **JS and assets**: `wwwroot/*.js` and `app.css` did not move, so the importmap fingerprints and the `pages.yml` `?v=` sed are unchanged. `VirtualList.razor.js` moved with its component; the import is `./Shared/VirtualList.razor.js`. The trimmed Release publish was run and used (screenshots `docs/screenshots/2026-11-22-client-folders/`).
- **Test projects**: `Core.Tests` (47), `Contracts.Tests` (3), `Storage.Tests` (7), `Scripting.Tests` (2), `Engine.Tests` (43), `Client.Tests` (72), `Ai.Tests` (6), `Cli.Tests` (36), `Beesharp.Tests` for the host (13): 229 as before. Files that tested several projects were split (`EnvironmentTests` into placeholders in Core, runs in Engine, the store in Storage; `CustomScriptScenarioTests` into parameter spec in Core, hooks in Engine, compile in Scripting; `CanceledBeesTests` into the runner part in Engine and the fake run in Client; the options tests of `AssistantTests` into Ai). The other assistant tests stay in the host project because they run the assistant through the host adapters and the engine's plan workspace. `Storage.Tests` has a `Microsoft.AspNetCore.App` framework reference only to get `BuildServiceProvider`. No shared test helper project was needed.
- **CI**: the test step logs with `trx;LogFilePrefix=results` (one TRX per project; a fixed file name would be overwritten); `.github/scripts/test-summary.sh` labels each TRX by its test assembly and prints a total.
- **Not done (follow-up)**: splitting the big files (`ScenarioDetailDialog.razor` 1300 lines, `Overview.razor` 870, `dock-magnify.js` 1070) into smaller pieces; they hold shared local state, so a split is not purely mechanical.
