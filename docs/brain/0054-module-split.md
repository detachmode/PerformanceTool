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

## Open (ask the user, one at a time)
- More details; answers get recorded above.
