# 0003. Scenarios are defined in C# code, configured in the UI

- Status: Accepted
- Date: 2026-11-20

## What
A scenario is a class implementing `IScenario` (`Services/Scenarios/IScenario.cs`): a stable `Id`, a name,
a description, a list of typed `ScenarioParameter`s (Text, Url, Integer, Multiline, Choice) and
`ExecuteAsync(ScenarioContext, CancellationToken)`. Scenarios are registered in `Program.cs` with
`builder.Services.AddScenario<T>()`, and `ScenarioCatalog` exposes them. The UI renders inputs from the
parameter list, so a new scenario needs no UI code.

Built-ins (`Services/Scenarios/BuiltIn`): HTTP GET, HTTP POST (JSON), custom HTTP request, user journey.

## Why
- The requirement was "specify scenarios in the code". Code can do anything (multi-step flows, auth,
  computed payloads) without inventing a scripting language.
- Parameters keep scenarios reusable: one class, many configurations, mixed in one run.
- Timing is measured by the runner around `ExecuteAsync`, so every scenario is measured the same way.
- One execution = one bar, so a multi-step journey shows as one bar (its total time).

## Consequences
- `Id` is persisted in presets. **Never rename an Id**; add a new scenario instead. Presets that reference
  a removed Id load without that entry and show a warning.
- Parameters added later get their default when an old preset is loaded (`ScenarioRunConfig.Normalize`).
- Scenarios receive a shared `HttpClient` per run; they must not dispose it and should honour the token
  (the token carries the per-execution timeout).
