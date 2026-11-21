# 0042. Custom C# script scenarios, lifecycle hooks and Fake/Live UI toggle

- Status: Accepted, implemented
- Date: 2026-11-21

## What
1. **Lifecycle hooks on `IScenario`** (`src/PerformanceTool.Core/Services/Scenarios/IScenario.cs`, `LoadRunner.cs`):
   - `StartupAsync(object parameters, ScenarioContext context, CancellationToken ct)` runs once per scenario entry before any actors start.
   - `ExecuteAsync(...)` runs per actor, sharing `context.State` (`ConcurrentDictionary<string, object?>`) populated during startup.
   - `TeardownAsync(...)` runs once per scenario entry in a `finally` block after all actors finish and background jobs are cancelled.
   - `context.RunBackground(Func<CancellationToken, Task>)` starts a run-scoped background loop (e.g. periodic OAuth/JWT token refresh) that writes refreshed tokens into `context.State` and is automatically cancelled and awaited before `TeardownAsync`.
2. **Custom C# script scenarios** (`ScriptScenarioStore.cs`, `ScriptScenarioBuilder.cs`, `Program.cs`):
   - `POST /api/scripts` accepts a `.csx`/`.cs` file upload (`multipart/form-data`) or JSON `{ fileName, code }`, compiles it on the server with Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`), registers the scenario in `ScenarioCatalog`, and persists it under `Scripts:Directory`.
   - Scripts receive an injected `Scenario` global (`Scenario.Define<TParams>("Name", async (p, ctx) => ...).OnStartup(...).OnTeardown(...)`). `ParameterSpec` supports positional records with default values and `[Description]`, and `ParameterSpec.FromJsonSchema` lets the Blazor WASM client render the generated parameter form without having the script assembly.
   - `POST /api/scripts/{id}/test-run` runs a single actor (`StartupAsync` → `ExecuteAsync` → `TeardownAsync`) and returns status, timings, recorded HTTP calls and logs.
3. **Fake / Live mode toggle in the UI** (`DataModeState.cs`, `SwitchableSources.cs`, `MainLayout.razor`, `CustomScriptsDialog.razor`):
   - A top-bar button toggles between **Fake mode** (in-browser simulation, default for GitHub Pages) and **Live mode** (talking to the ASP.NET Core server API).

## Why
- **C# scripting vs DLL plugin loading:**
  - **C# scripting (Roslyn)** is ideal for fast iteration: upload or edit a single `.csx` file in the UI/API, compile in-process in milliseconds, get line/column diagnostics immediately, and reflect `TParams` into the generated Blazor form with zero build pipeline or `AssemblyLoadContext` unload headaches.
  - **DLL loading (`AssemblyLoadContext`)** is complementary for large teams that already maintain a compiled C# class library with custom NuGet dependencies (e.g. gRPC/database SDKs, shared domain models). Because both produce `IScenario` instances with `ParameterSpec` and the same `StartupAsync` / `ExecuteAsync` / `TeardownAsync` lifecycle, DLL loading can be added later without changing the runner, UI or API.
- **Background token refresh:** Starting one background loop in `StartupAsync` via `ctx.RunBackground(...)` and publishing the current token to `ctx.State["token"]` ensures hundreds of concurrent actors reuse one fresh token without stampeding the auth server, and the runner guarantees clean cancellation before `TeardownAsync`.

## Consequences
- `PerformanceTool.Core` stays free of Roslyn and ASP.NET dependencies; Roslyn scripting lives only in the server project (`PerformanceTool`).
- Uploaded C# scripts run in-process with full server trust (intended for trusted local/internal load-testing environments, not untrusted multi-tenant hosting).
- `GET /api/scenarios` includes `isCustom` and `scriptId` alongside the JSON Schema so the WASM client dynamically registers custom scenarios via `RemoteSchemaScenario`.
