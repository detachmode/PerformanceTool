# Context: domain language

The shared vocabulary of PerformanceTool. Use these words, with these meanings, in code, UI, docs and
conversations. When a term changes, update it here first.

**Status** of each term:
- **Decided**: confirmed by the user.
- **Current**: how the code uses it today; not yet confirmed.
- **Proposed**: an idea, not in use yet.

## Guideline
This tool has its **own** language and its own view of what matters. Don't default to the vocabulary or metrics of
standard load-testing tools (requests, iterations, virtual users, throughput, p95 everywhere…). Start from
scenarios, actors and calls, and ask the user when unsure.

## Colours
Bars and status: **green = good / positive** (also Information log lines), **yellow = slow**, **red = failed**; actors **in flight** are shown as running (blue/grey and/or animated, style open) (brain 0018). No colour per scenario.

## Ordering rule
Where actors are listed or grouped: **failed → slow → successful**, then by time (brain 0028). Actor numbers always show their outcome colour.

## Terms

| Term | Meaning | Code | Status |
|---|---|---|---|
| **Actor** | One actor plays a scenario (story) through **once**: one run-through of the scenario's code. **One actor = one bar** in the charts = **one logging scope**. An actor makes several **calls**. Has a start time, duration, outcome and an optional error. An actor in flight when the user stops the run ends as **canceled** (brain 0053): it keeps its logs and calls, the cut-off call is failed, and it looks and counts like failed with the error "Canceled: run stopped". (Not the actor-model sense of Orleans/Akka/Dapr: ours is short-lived.) | today `ExecutionResult`, `ScenarioOutcome`, `IScenario.ExecuteAsync` (to be renamed) | **Decided** (replaces "instance", which replaced "execution") |
| **Call** | One request an actor makes, e.g. one HTTP request; calls belong to a **step** when the scenario declares steps. A scenario chains many calls (up to ~50); together they make up the actor's run-through. | recorded automatically from `HttpClient` (DelegatingHandler, brain 0013) | **Decided** |
| **Step** | A named part of a scenario's story (e.g. *Log in*, *Search*, *Pay*), **declared before the run**, so the UI shows "step 2 of 4" for a bee in flight, the step a failed bee stopped at, and steps not reached. Groups the calls made in it. | C# 15 `union` step machine (`Scenario<TParameters, TStep>`, `Scenario.DefineSteps`, `StepInfo`, `StepUnionReflection`) | **Decided** (brain 0056) |
| **Logging scope** | The `ILogger` scope created for each actor, so every log line belongs to exactly one actor (one bar). | — (not implemented) | **Decided** (brain 0012) |
| **Scenario** | **A whole user story**, defined in code with typed parameters. Example: *search for a product, open 5 of the search results, add 2 of them to the shopping cart*. One story = one scenario; the individual requests in it are its **calls**. Not a single endpoint (the built-in "HTTP GET" etc. are only low-level building blocks). | `IScenario`, `Scenario<TParameters>` | **Decided** (story level) |
| **Scenario parameters** | The typed settings of a scenario; the editor is generated from them. | `TParameters`, `ParameterSpec` | Current |
| **Scenario entry** | A scenario as configured inside a mix: label, parameter values and rate. The same scenario can appear several times with different settings. In the UI it's often just called "a scenario". | `ScenarioRunConfig` | Current |
| **Scenario mix / load plan** | All entries plus duration, max in-flight and timeout. Two names for the same thing. | `LoadPlan` | Current |
| **Environment** | A named set of key-value pairs (base URL, tenant, token, ...) that scenarios run against; one is active at a time ("No environment" is allowed). Values may be credentials. One JSON file per environment. | `EnvironmentDefinition`, `EnvironmentStore` | **Decided** (brain 0033) |
| **Placeholder** | `{{Key}}` in a scenario parameter value; the active environment fills it in when the run starts. Unknown keys are left as written and reported. Scenario code reads values with `context.Environment["Key"]`. Not the same as `${env:NAME}` (operating-system variable for secret fields). | `Placeholders` | **Decided** (brain 0033) |
| **Static values** | The selected environment's key-value pairs; fixed for the whole run. Shown in the Environment tab. | `EnvironmentValues` | **Decided** (brain 0052) |
| **Runtime data** | Key-value data that the preflight, scenarios and the shutdown program read and write **during** a run. A finished run keeps the final values. Two scopes: the **run bag** (shared by the whole run) and one **entry bag** per scenario entry. | `RunData` / `BagAccess` (Core `Services/Runtime/`), `ScenarioContext.RunBag` (run bag) and `.State` (entry bag), `Data` in preflight and shutdown scripts | **Decided** (brain 0052) |
| **Preflight** | A C# program that runs before the scenarios start; they only begin if it succeeds. | `PreflightStore`, `LoadPlan.PreflightScriptId` | Current |
| **Shutdown program** | A C# program that always runs at the end of a run (normal end, stop, or failed preflight), e.g. to clean up. | `LoadPlan.ShutdownScriptId`, `ShutdownStore` (`scripts/shutdown/`, same machinery as `PreflightStore`), run in `RunManager` after the run ends | **Decided** (brain 0052) |
| **Preset** | A saved, named load plan. | `Preset`, `PresetStore` | Current |
| **Run (load run)** | Executing a whole load plan once, from start until the duration ends or the user stops it. Contains many actors. | `LoadRunner.RunAsync` | Current |
| **Rate** | How many new actors start per minute for a scenario entry: **actors per minute**. UI label "Actors / minute" (short "actors/min"). Never "runs / minute": a run is the whole load run. | `RatePerMinute` | **Decided** (follows the Actor rename) |
| **Spawning pattern** | How a scenario entry starts new actors over time: **linear** or **bursts**. Default linear. | `ScenarioRunConfig.Pattern`, `SpawningPattern` | **Decided** (brain 0034) |
| **Linear** | Spawning pattern: actors start evenly spread, at the entry's **actors per minute**. | `SpawningPattern.Linear`, `RatePerMinute` | **Decided** (brain 0034) |
| **Burst** | Spawning pattern: **actors per burst** start at once, **bursts per minute** times a minute. Effective rate = actors per burst x bursts per minute. UI shows e.g. "15 × 6/min". | `SpawningPattern.Bursts`, `ActorsPerBurst`, `BurstsPerMinute`, `EffectiveRatePerMinute()` | **Decided** (brain 0034) |
| **In flight** | Actors that have started and not finished yet ("running"). | in-flight counts | Current |
| **Max in-flight** | Upper limit of actors in flight across the run. | `MaxConcurrency` | Current |
| **Dropped** | An actor that was due but not started because max in-flight was reached. | `onDropped` | Current |
| **Timeout** | Maximum duration of one actor; it then counts as failed. | `TimeoutSeconds` | Current |
| **Tile / scenario card** | The small box per entry on the overview, with a value and a little box chart. | `ScenarioTile` | Current |
| **Details popup** | Opens from a tile; shows everything about one entry (stats, charts, errors, parameters, logs). | `ScenarioDetailDialog` | Current |
| **Full artifact / summary artifact** | The two CI artifacts per run: everything (incl. logs) vs a small overview. | — | **Decided** (brain 0010) |
| **Assistant** | The chat in the right side panel (Live mode, when configured): an AI agent that can do what the user can do in the UI (edit the plan, run, read runs, bees and logs) and shows data as small cards. Runs every tool directly, no approval. Its tools speak this language. | `Beesharp.Ai`, `AssistantService` | Proposed (brain 0047, 0048, 0049) |
| **Run bundle** | The self-describing folder of files a run produces. | — | Proposed (brain 0009) |
