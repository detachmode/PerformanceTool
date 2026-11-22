# 0053. Bees in flight when a run is stopped are kept as "canceled", with their logs

- Status: Accepted (goal), details open
- Date: 2026-11-22
- Supersedes: the line "Stopping a run cancels in-flight executions; those are not reported" in 0004

## What (user decision)
When the user stops a run, the bees still in flight get the status **canceled** and **keep the logs they already
got**. Today they are dropped without a trace (server `LoadRunner.ExecuteOne` returns without a result; the fake
`FakeRunSource.Finish` clears them).

- **Look and count like failed** (user answer, 2026-11-22): red, counted in the errors, ordered with the failed bees,
  with the error text "Canceled: run stopped" (so the error dictionary groups them).

## Open (ask the user, one at a time)
- Whether calls already made (and a call cut off mid-way) are kept like the logs.

## Consequences
- A third outcome next to success and failed: `ScenarioOutcome`, `ExecutionResult`, storage, wire format, the
  client's `BeeRecord`, filters and charts must know it.
