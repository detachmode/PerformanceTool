# 0004. Open-model scheduling: runs per minute per scenario, drop when saturated

- Status: Accepted
- Date: 2026-11-20

## What
A `LoadPlan` (`Services/LoadPlan.cs`) has entries, each with `RatePerMinute`, plus a duration, max
in-flight executions and a timeout. `LoadRunner` gives each entry its own scheduler that fires at absolute
times `n × (1 min / rate)` from the run start, independent of how long earlier executions took. If
`MaxConcurrency` executions are already in flight, that run is **dropped and counted** ("Dropped").

## Why
- "How often per minute" is an arrival rate, i.e. an *open* workload model. Real users don't wait for
  other users' requests to finish.
- A closed loop ("N workers back to back") slows down when the target slows down, which hides latency
  (coordinated omission). Fixed arrivals keep the pressure honest.
- Absolute due-times avoid drift. Each due time is computed from `n` in ticks (rounded), not as
  `n × interval`: a rounded interval multiplied up drifts and produced one extra run (25 instead of 24).
- Dropping instead of queueing keeps memory bounded and makes saturation visible instead of silently
  turning into a queue.

## Consequences
- "Dropped > 0" means the tool (or target) is saturated at that in-flight limit; raise it or lower rates.
- The per-execution timeout is a linked `CancellationToken` (not `HttpClient.Timeout`) so multi-step
  scenarios are bounded as a whole.
- Stopping a run cancels in-flight executions; those are not reported.
