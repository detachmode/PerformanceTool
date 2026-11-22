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
