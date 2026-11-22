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

## Implementation notes (Proposed, Claude)
- **Data model:** `ExecutionResult.Canceled` (flag, default false) with the error `ExecutionResult.CanceledError` = "Canceled: run stopped". `ScenarioOutcome` is unchanged: the runner builds the failed outcome itself. Storage and the wire format carry only the error text (the stored outcome stays 2 = failed); `ExecutionResult.IsCanceledError` and the client's `BeeRecord.Canceled` recognise a canceled bee again from that text, so no schema change and old runs stay readable. Proposed: if "canceled" should ever be filtered on its own, add a stored outcome 3 then.
- **Server:** `LoadRunner.ExecuteOne` catches any exception while the run token is cancelled (also a wrapped `TaskCanceledException`), reports the bee as canceled with its logs and calls, then the activity end follows as before (every start has one end). Results are reported before `RunAsync` returns, so they are stored before the shutdown program and the run finish.
- **Calls:** `ScenarioContext.MeasureCallAsync` records the cut-off call as failed with its duration so far; when the run (not the per-bee timeout) was stopped its error is "Canceled: run stopped" (`ScenarioContext` got a `runStopped` callback). The UI shows its status as "canceled" instead of "timeout". A bee that ignores its token keeps running until it finishes and then ends normally, as before.
- **Fake mode:** `FakeRunSource.Finish` turns the pending bees into canceled bees (`Cancel`): calls up to the stop stay, the running call is failed at the stop, later calls are skipped, logs end with "B# canceled: run stopped".
- **UI:** no new look; they are failed bees. The AI assistant reports them as outcome "failed" with the error text.
- **Client fix found on the way:** `ApiRunSource.ApplyStatus` now ignores a bee sequence it already has (two overlapping polls delivered the first bees twice, which a stopped run made visible).
- **Follow-up:** the client polls with `afterSeq` = highest sequence seen, but the server lists bees in finishing order, so a bee that finishes after a higher-numbered one in a later poll can be missed. Canceled bees all end within milliseconds, so it is rare here, but a cursor by list position would be safer.
