# Context: domain language

The shared vocabulary of PerformanceTool. Use these words, with these meanings, in code, UI, docs and
conversations. When a term changes, update it here first.

**Status** of each term:
- **Decided**: confirmed by the user.
- **Current**: how the code uses it today; not yet confirmed.
- **Proposed**: an idea, not in use yet.

## Terms

| Term | Meaning | Code | Status |
|---|---|---|---|
| **Execution** | One single invocation of a scenario's code. **One execution = one bar** in the charts = **one logging scope**. Has a start time, duration, status code, bytes and an optional error. | `ExecutionResult`, `ScenarioOutcome`, `IScenario.ExecuteAsync` | **Decided** (bar and scope: brain 0012) |
| **Logging scope** | The `ILogger` scope created for each execution, so every log line belongs to exactly one execution (one bar). | — (not implemented) | **Decided** (brain 0012) |
| **Scenario** | A kind of load, defined in code as a class with typed parameters (e.g. "HTTP GET", "User journey"). | `IScenario`, `Scenario<TParameters>` | Current |
| **Scenario parameters** | The typed settings of a scenario; the editor is generated from them. | `TParameters`, `ParameterSpec` | Current |
| **Scenario entry** | A scenario as configured inside a mix: label, parameter values and rate. The same scenario can appear several times with different settings. In the UI it's often just called "a scenario". | `ScenarioRunConfig` | Current |
| **Scenario mix / load plan** | All entries plus duration, max in-flight and timeout. Two names for the same thing. | `LoadPlan` | Current |
| **Preset** | A saved, named load plan. | `Preset`, `PresetStore` | Current |
| **Run (load run)** | Executing a whole load plan once, from start until the duration ends or the user stops it. Contains many executions. | `LoadRunner.RunAsync` | Current |
| **Rate** | How often an entry is started, per minute. The UI labels it "Runs / minute", which clashes with **Run** above: it means executions per minute. | `RatePerMinute` | Current, **inconsistent** |
| **In flight** | Executions that have started and not finished yet. | in-flight counts | Current |
| **Max in-flight** | Upper limit of executions in flight across the run. | `MaxConcurrency` | Current |
| **Dropped** | An execution that was due but not started because max in-flight was reached. | `onDropped` | Current |
| **Timeout** | Maximum duration of one execution; it then counts as failed. | `TimeoutSeconds` | Current |
| **Tile / scenario card** | The small box per entry on the overview, with a value and a little box chart. | `ScenarioTile` | Current |
| **Details popup** | Opens from a tile; shows everything about one entry (stats, charts, errors, parameters, logs). | `ScenarioDetailDialog` | Current |
| **Full artifact / summary artifact** | The two CI artifacts per run: everything (incl. logs) vs a small overview. | — | **Decided** (brain 0010) |
| **Run bundle** | The self-describing folder of files a run produces. | — | Proposed (brain 0009) |
