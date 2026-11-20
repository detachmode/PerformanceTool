# 0012. Scenarios log via ILogger; each execution gets its own scope; logs are recorded and shown

- Status: Accepted (user decision, not implemented)
- Date: 2026-11-20

## What
- Scenarios write log statements through the standard **`ILogger`** interface (Microsoft.Extensions.Logging).
- **Each run gets a new logging scope injected** (e.g. run id, scenario entry, execution number), so every log
  statement can be traced back to where it came from.
- The tool **records** these logs and **persists them locally** with the run's data. In CI/CD they are published as
  artifacts later; they belong in the **full artifact** from 0010, not the small summary one.
- The **UI shows the log statements when inspecting a scenario** (the scenario details popup).

## Why
- `ILogger` is the standard .NET logging API, so scenario authors need nothing new to learn and can use existing
  libraries that log.
- Scopes tie log lines to individual executions, so failures and slow executions can be explained.
- Persisting the logs means people and agents can look at them after the run, also for CI runs.

## Open (to decide with the user)
- Does "run" here mean one scenario **execution**, or a whole **load run**? (Asked; not decided yet.)
- Which log levels are recorded, limits for very chatty scenarios, and the storage format.
