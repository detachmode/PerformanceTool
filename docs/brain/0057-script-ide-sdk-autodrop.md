# 0057. IDE support for custom scenario scripts via .NET 11 file-based apps and SDK auto-drop

- Status: Accepted, implemented
- Date: 2026-11-23

## What
Custom scenario scripts can be authored and debugged as standalone .NET 11 file-based apps (`dotnet run my-scenario.cs` or IDE F5/IntelliSense) while remaining 100% compatible with Beesharp's Roslyn script host:

- **`Beesharp.Core` NuGet package** (`src/Beesharp.Core/Beesharp.Core.csproj`, `buildTransitive/Beesharp.Core.props`): generates XML documentation (`Beesharp.Core.xml`) and ships a `buildTransitive` props file that adds the same global usings as `ScriptCompiler` + `ScriptScenarioStore`.
- **Static `Scenario` entry point & `DebugRunAsync`** (`src/Beesharp.Core/Services/Scenarios/ScriptScenarioBuilder.cs`): `public static class Scenario` mirrors `ScenarioScriptHost`'s `Define` / `DefineSteps` overloads via an `AsyncLocal<ScenarioScriptHost?>` scope (`Scenario.BeginHostScope`). `ScenarioBuilder<T>.DebugRunAsync(args)` is a no-op during host registration and runs a single bee with `RecordingHttpHandler`, `--param Key=Value` overrides, and stdout diagnostics when executed standalone.
- **File-based app directive stripping** (`src/Beesharp.Scripting/ScriptCompiler.cs`, `ScriptScenarioStore.cs`): `ScriptCompiler.StripFileBasedAppDirectives` replaces `#:package`, `#:project`, `#:sdk`, and `#:property` lines with empty lines so Roslyn diagnostic and stack-trace line numbers remain exact. `ScenarioScriptGlobals` exposes `string[] args` so `await scenario.DebugRunAsync(args);` compiles inside `CSharpScript`.
- **Automatic `.beesharp/` SDK drop** (`src/Beesharp.Scripting/ScriptSdkDropper.cs`, invoked by `LoadBeesharpScriptsAsync` in `src/Beesharp.Engine/EngineRegistration.cs`): copies the bundled `Beesharp.Core.*.nupkg`, `Beesharp.Core.dll`, and `Beesharp.Core.xml` into `<scriptsDir>/.beesharp/` (removing stale package versions), writes `<scriptsDir>/.beesharp/.gitignore`, and creates a minimal `<scriptsDir>/NuGet.config` pointing at `./.beesharp` if none exists. Read-only folders (e.g. `:ro` Docker mounts) and missing SDK sources are skipped silently.
- **Build, Docker & NuGet packaging** (`Dockerfile`, `src/Beesharp/Beesharp.csproj`, `.github/workflows/nuget.yml`): packs `Beesharp.Core` (`0.1.0`) into `/app/sdk` (Docker) and `$(OutputPath)sdk` (local builds), and publishes to NuGet.org on `v*` tags or manual dispatch so fresh clones and CI without Docker can fall back to NuGet.org.

## Why
Users editing custom scenario `.cs` scripts in VS Code, Rider, or Visual Studio had no IntelliSense, no XML docs, and no way to set breakpoints or run a single bee locally without uploading to the server. Auto-dropping the exact matching `.nupkg` into the scripts folder makes `#:package Beesharp.Core@0.1.0` work offline, locally, and against bind-mounted Docker volumes with zero manual setup, while publishing `Beesharp.Core` to NuGet.org via `.github/workflows/nuget.yml` keeps fresh clones and CI working even when Beesharp is not running in Docker (since `NuGet.config` adds `./.beesharp` without `<clear />`).

Alternatives rejected:
- Generating a `.csproj` in the scripts folder: adds noise, conflicts with user-managed projects, and breaks single-file simplicity.
- Requiring users to push/configure an external NuGet feed: unnecessary friction and version drift against the running server/container.

## Consequences
- Existing scripts without `#:package` or `DebugRunAsync` continue to compile and run untouched.
- `.beesharp/` is git-ignored (both in the repo root `.gitignore` and inside `.beesharp/.gitignore`); `NuGet.config` is only created if absent and never overwritten.
- When `Beesharp.Core`'s `<Version>` is bumped, `ScriptSdkDropper` automatically cleans up older `Beesharp.Core.*.nupkg` files in `.beesharp/` and `PackBeesharpCoreSdk`'s `Outputs` in `Beesharp.csproj` should be updated to match.
