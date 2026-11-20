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
scenarios, instances and calls, and ask the user when unsure.

## Terms

| Term | Meaning | Code | Status |
|---|---|---|---|
| **Instance** | One instance of a scenario: one run-through of the scenario's code. **One instance = one bar** in the charts = **one logging scope**. An instance can contain several **calls**. Has a start time, duration, outcome and an optional error. | today `ExecutionResult`, `ScenarioOutcome`, `IScenario.ExecuteAsync` (to be renamed) | **Decided** (replaces "execution") |
| **Call** | One step inside an instance, e.g. one HTTP request. A scenario can chain multiple calls; together they make up the instance. | — (inside scenario code, e.g. user journey steps) | **Decided** |
| **Logging scope** | The `ILogger` scope created for each instance, so every log line belongs to exactly one instance (one bar). | — (not implemented) | **Decided** (brain 0012) |
| **Scenario** | A kind of load, defined in code as a class with typed parameters (e.g. "HTTP GET", "User journey"). | `IScenario`, `Scenario<TParameters>` | Current |
| **Scenario parameters** | The typed settings of a scenario; the editor is generated from them. | `TParameters`, `ParameterSpec` | Current |
| **Scenario entry** | A scenario as configured inside a mix: label, parameter values and rate. The same scenario can appear several times with different settings. In the UI it's often just called "a scenario". | `ScenarioRunConfig` | Current |
| **Scenario mix / load plan** | All entries plus duration, max in-flight and timeout. Two names for the same thing. | `LoadPlan` | Current |
| **Preset** | A saved, named load plan. | `Preset`, `PresetStore` | Current |
| **Run (load run)** | Executing a whole load plan once, from start until the duration ends or the user stops it. Contains many instances. | `LoadRunner.RunAsync` | Current |
| **Rate** | How often an entry starts a new instance: **instances per minute**. UI label "Instances / minute" (short "inst/min"). Never "runs / minute": a run is the whole load run. | `RatePerMinute` | **Decided** (follows the Instance rename) |
| **In flight** | Instances that have started and not finished yet. | in-flight counts | Current |
| **Max in-flight** | Upper limit of instances in flight across the run. | `MaxConcurrency` | Current |
| **Dropped** | An instance that was due but not started because max in-flight was reached. | `onDropped` | Current |
| **Timeout** | Maximum duration of one instance; it then counts as failed. | `TimeoutSeconds` | Current |
| **Tile / scenario card** | The small box per entry on the overview, with a value and a little box chart. | `ScenarioTile` | Current |
| **Details popup** | Opens from a tile; shows everything about one entry (stats, charts, errors, parameters, logs). | `ScenarioDetailDialog` | Current |
| **Full artifact / summary artifact** | The two CI artifacts per run: everything (incl. logs) vs a small overview. | — | **Decided** (brain 0010) |
| **Run bundle** | The self-describing folder of files a run produces. | — | Proposed (brain 0009) |
