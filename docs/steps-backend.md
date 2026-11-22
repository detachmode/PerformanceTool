# Scenario steps: what the backend still needs

Status: **Proposed** (Claude's spec, brain 0056). The UI draft with fake data is merged (PR #28); nothing below exists
on the server yet. In Live mode the client shows no steps today.

## A. Decisions for the user first
These change the shape of the code, so they come before any implementation.

1. **How steps are written in code.** (a) `enum` + `ctx.Step(...)`, linear code with local variables, or
   (b) C# 15 `union` of step records as a step machine (`RunStepAsync(step) => next step`), or (c) a typed chain.
   Scripts need the same choice (`Scenario.Define(...).Steps(...)`).
2. **Going back.** Repeat a step only in a row (*Open product* ×5), or also go back to an earlier step
   (Search, Open, Search, Open)? Option (b) allows going back for free.
3. **"2 of 5" needs the count up front.** In real code the count usually comes from a parameter or data
   (`hits.Take(p.OpenCount)`). Either the step declares its count (e.g. from the parameters) or the UI shows
   "Open product, 2nd time" without the total.
4. **Where a failure belongs when it is outside a step** (the bee throws between steps, times out, is canceled, or
   returns a failed outcome after the last step). Proposal: the step that started last; before the first step: step 1.
5. **Unions in the build.** If (b): check that unions are stable in .NET 11 (no `LangVersion=preview`) and allow
   `builds.dotnet.microsoft.com` in the agent environment so agents can compile .NET 11 code.

## B. What is missing, per module
Names below are suggestions; the REST shape follows `Beesharp.Contracts` rules (one type, no duplicates).

### Core (`src/Beesharp.Core`)
- **Declaring steps:** `IScenario.Steps` (`IReadOnlyList<StepInfo>`, empty = no steps). `StepInfo(Name, int? Times)`.
  Filled from the enum / union cases / `.Steps(...)` once when the scenario is built, never per bee.
- **Running a step:** `ScenarioContext.Step(...)` (or the step machine loop). It sets the bee's current step and
  repetition (two ints, `Volatile` writes: read by the live poll), times the step, and on an exception records the
  failed step and repetition, then rethrows.
- **Calls and logs know their step:** `RecordedCall` gets `Step` and `Rep` (0 for scenarios without steps);
  `RecordedLog` gets `Step` (like `CallIndex`).
- **Bee result:** `ExecutionResult.FailedStep` / `FailedRep` (-1 = none), set using rule A4.
- **Validation:** a step not in the declared list, or more repetitions than declared, fails the bee with a clear error
  (and shows in the test run).
- **Gap found while writing this:** in load runs only calls made with `MeasureCallAsync`/`RecordCall` are recorded.
  Plain `ctx.Http` requests are not (the `DelegatingHandler` of brain 0013 exists only in script test runs,
  `ScriptScenarioStore.RecordingHttpHandler`). Steps group calls, so the runner should record HTTP calls the same way.
- **Built-in scenarios:** *User journey* can declare one step per URL (optional, small).

### Scripting (`src/Beesharp.Scripting`)
- `ScenarioBuilder.Steps(...)` (or union support) in `ScriptScenarioBuilder.cs`; script compile errors for unknown
  step names where the compiler can see them.
- The test run result (`ScriptTestRunResult`) returns the steps with their calls, so the editor's test run can show
  the stepper.

### Engine (`src/Beesharp.Engine`)
- `LoadRunner`: pass `FailedStep`/`FailedRep` through; give `RunManager` a way to read a running bee's current
  step and repetition (e.g. keep the bee's `ScenarioContext` or a small `BeeProgress` per sequence).
- `RunManager`: per entry, counters for **failed per step** (incremental, like `slowCalls`) and, on each poll,
  **in flight per step** from the running bees.
- Stored runs must keep **the steps as they were** (the scenario code can change later): save the declared steps
  per entry with the run (next to `plan_json`).

### Contracts (`src/Beesharp.Contracts`) and REST
- `ScenarioInfoDto.Steps` (`GET /api/scenarios`): the declared steps, so the editor and popup know them before a run.
- `LiveBeeDto.FailedStep`, `FailedRep`.
- `LiveRunningBeeDto.Step`, `Rep` (the "Step 3 of 5 · Open product (2 of 5)" line).
- `LiveScenarioViewDto.FailedPerStep`, `InFlightPerStep` (int arrays, empty without steps).
- `StoredBeeDetail`: calls and logs carry `Step`/`Rep` (via `RecordedCall`/`RecordedLog`), plus `FailedStep`/`FailedRep`.
- Stored run: the steps per entry (for `GET /api/runs/{id}`).
- Filter: `GET /api/runs/{id}/bees?failedStep=n&entry=e` (and the same for the live run) for "failed at Pay".
- `ContractsRoundTripTests` for every new property.

### Storage (`src/Beesharp.Storage`, `src/Beesharp.Storage.DuckDb`)
- DuckDB: `calls.step`, `calls.rep`, `logs.step`, `bees.failed_step`, `bees.failed_rep`; a `run_steps` table
  (run_id, entry, step, name, times) or JSON next to `plan_json`; index for the failed-step filter.
  `CurrentSchemaVersion` 1 → 2 with a migration that adds the columns (old runs: no steps).
- `InMemoryRunStorage`: the same fields.
- Exports (parquet/duckdb/json/csv) include the new columns.

### Client Live mode (`src/Beesharp.Client/Data/ApiRunSource.cs`, `RemoteBeeDetail.cs`)
- Map the new DTO fields to what the draft already uses: `RunSnapshot.Steps` (`ScenarioSteps`),
  `BeeRecord.FailedStep`, `FakeCall.Step`/`Rep`, the running bee's position, the per-step counts.
- No UI changes expected; the draft's `Runs/ScenarioSteps.cs` stays the one place for step logic.

### Command-line tool (`src/Beesharp.Cli`)
- `RunSummary`: per entry "failed at Pay: 12" in the text, and `failedPerStep` in `--json`. Exit codes unchanged.

### Assistant (`src/Beesharp.Ai`)
- `get_scenario`/`list_scenarios` show the steps; `list_bees`/`show_bees` filter by failed step; `get_bee`/`show_bee`
  return the bee's steps. Tool descriptions number steps from 1.

### Docs and tests
- `docs/context.md`: Step from Proposed to Decided once the user decides; `AGENTS.md`: how to declare steps and where
  the step logic lives. Brain 0056 updated with the decisions.
- Tests in the project that owns the behaviour: Core (step tracking, failure rule, validation), Scripting (`.Steps`),
  Engine (counts, in-flight step, canceled bee keeps its step), Storage (columns, migration, filter), Contracts
  (round trip), Cli (summary), Ai (tools), Client (mapping in `ApiRunSource`).

## C. Suggested order (small steps, brain 0020)
1. Decisions A1 to A4 (and A5 if unions).
2. Core + Engine + Contracts: declare, track, report live (Live mode shows steps for running bees and failures).
3. Storage + stored runs + filter endpoint.
4. Scripting `.Steps(...)` and the test run stepper.
5. HTTP calls recorded in load runs (the gap above), if not done in 2.
6. CLI summary and assistant tools.
