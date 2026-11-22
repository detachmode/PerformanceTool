# 0053. Bees in flight when a run is stopped are kept as "canceled", with their logs

- Status: Accepted
- Date: 2026-11-22
- Supersedes: the line "Stopping a run cancels in-flight executions; those are not reported" in 0004

## What (user decision)
When the user stops a run, the bees still in flight get the status **canceled** and **keep the logs they already
got**. Today they are dropped without a trace (server `LoadRunner.ExecuteOne` returns without a result; the fake
`FakeRunSource.Finish` clears them).

- **Look and count like failed** (user answer, 2026-11-22): red, counted in the errors, ordered with the failed bees,
  with the error text "Canceled: run stopped" (so the error dictionary groups them).

- **Calls are kept too** (user answer, 2026-11-22): finished calls stay as they are; the call cut off by the stop is
  kept as failed, with the time it ran until the stop. The bee's duration ends at the stop.

## Consequences
- A third outcome next to success and failed: `ScenarioOutcome`, `ExecutionResult`, storage, wire format, the
  client's `BeeRecord`, filters and charts must know it.
