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

## Terms

| Term | Meaning | Code | Status |
|---|---|---|---|
| **Actor** | One actor plays a scenario (story) through **once**: one run-through of the scenario's code. **One actor = one bar** in the charts = **one logging scope**. An actor makes several **calls**. Has a start time, duration, outcome and an optional error. (Not the actor-model sense of Orleans/Akka/Dapr: ours is short-lived.) | today `ExecutionResult`, `ScenarioOutcome`, `IScenario.ExecuteAsync` (to be renamed) | **Decided** (replaces "instance", which replaced "execution") |
| **Call** | One step an actor makes, e.g. one HTTP request. A scenario chains many calls (up to ~50); together they make up the actor's run-through. | recorded automatically from `HttpClient` (DelegatingHandler, brain 0013) | **Decided** |
| **Logging scope** | The `ILogger` scope created for each actor, so every log line belongs to exactly one actor (one bar). | — (not implemented) | **Decided** (brain 0012) |
| **Scenario** | **A whole user story**, defined in code with typed parameters. Example: *search for a product, open 5 of the search results, add 2 of them to the shopping cart*. One story = one scenario; the individual requests in it are its **calls**. Not a single endpoint (the built-in "HTTP GET" etc. are only low-level building blocks). | `IScenario`, `Scenario<TParameters>` | **Decided** (story level) |
| **Scenario parameters** | The typed settings of a scenario; the editor is generated from them. | `TParameters`, `ParameterSpec` | Current |
| **Scenario entry** | A scenario as configured inside a mix: label, parameter values and rate. The same scenario can appear several times with different settings. In the UI it's often just called "a scenario". | `ScenarioRunConfig` | Current |
| **Scenario mix / load plan** | All entries plus duration, max in-flight and timeout. Two names for the same thing. | `LoadPlan` | Current |
| **Preset** | A saved, named load plan. | `Preset`, `PresetStore` | Current |
| **Run (load run)** | Executing a whole load plan once, from start until the duration ends or the user stops it. Contains many actors. | `LoadRunner.RunAsync` | Current |
| **Rate** | How many new actors start per minute for a scenario entry: **actors per minute**. UI label "Actors / minute" (short "actors/min"). Never "runs / minute": a run is the whole load run. | `RatePerMinute` | **Decided** (follows the Actor rename) |
| **In flight** | Actors that have started and not finished yet ("running"). | in-flight counts | Current |
| **Max in-flight** | Upper limit of actors in flight across the run. | `MaxConcurrency` | Current |
| **Dropped** | An actor that was due but not started because max in-flight was reached. | `onDropped` | Current |
| **Timeout** | Maximum duration of one actor; it then counts as failed. | `TimeoutSeconds` | Current |
| **Tile / scenario card** | The small box per entry on the overview, with a value and a little box chart. | `ScenarioTile` | Current |
| **Details popup** | Opens from a tile; shows everything about one entry (stats, charts, errors, parameters, logs). | `ScenarioDetailDialog` | Current |
| **Full artifact / summary artifact** | The two CI artifacts per run: everything (incl. logs) vs a small overview. | — | **Decided** (brain 0010) |
| **Run bundle** | The self-describing folder of files a run produces. | — | Proposed (brain 0009) |
