# 0058. Embedded `Beesharp.Host` (Aspire-style project) and default load plan

- Status: Proposed
- Date: 2026-11-24

## What
Two complementary pieces for teams whose load tests grow beyond single-file scripts (brain 0057) into a multi-file C# suite with shared auth, helpers, DTOs, or third-party NuGet packages:

1. **Embedded `Beesharp.Host` NuGet package (Aspire-style):**
   - A user creates a normal console `.csproj` (e.g. `MyApp.LoadTests.csproj`) referencing `Beesharp.Host` plus any NuGet packages or `<ProjectReference>`s to their own app's contracts/DTOs.
   - `Beesharp.Host` bundles the engine, REST API, DuckDB/in-memory storage, and the pre-built **Blazor WebAssembly UI static assets** — so `dotnet run` / IDE **F5** launches the full Beesharp UI directly in the user's process with **no Docker required** and native breakpoint debugging in every scenario.
   - Shared cross-cutting concerns (like an OAuth `AuthTokenCache` with `SemaphoreSlim` refresh or typed HTTP clients) use **plain .NET DI** (`builder.Services.AddSingleton<AuthTokenCache>()` + constructor injection into `Scenario<TParams, TStep>`) — no new Beesharp `IRunService` abstraction.
   - The same executable can run headless in CI (`dotnet run -- run --preset smoke --env staging`) using the CLI runner, or be published into a slim `aspnet:11.0` container without needing the .NET SDK in Docker.

2. **Pre-configured default plan & environment (`appsettings.json` / presets / fluent C#):**
   - So clicking **Play** immediately runs the intended scenarios without manual setup after a fresh start:
     - `appsettings.json` supports `"Plan:DefaultPreset"` (loads `presets/<name>.json`) and `"Plan:DefaultEnvironment"`, or an inline `"Plan": { ... }` section (overridable via `Plan__*` environment variables).
     - `BeesharpApp.CreateBuilder(args)` can also register strongly typed presets in C# (`builder.AddPreset("Smoke", ...).AsDefault()`) that appear in the UI's preset list and remain editable in the Editor.
   - Applied when seeding `PlanWorkspace` on startup if no prior run was restored (or when explicitly configured).

## Why
Single-file scripts (`#:package Beesharp.Core@0.1.0`) are great for standalone scenarios, but multi-scenario suites need shared auth caches, typed API clients, shared DTOs, and arbitrary NuGet packages.
- **Why embedded `Beesharp.Host` over `#:include` stitching or a `sdk:11.0` Docker compiler:** avoids reinventing MSBuild/include resolution, keeps Docker images slim, gives 100% native IDE refactoring and F5 debugging, and keeps the UI and engine version-locked with zero CORS/sidecar setup.
- **Why plain .NET DI instead of a custom `IRunService` interface:** standard `AddSingleton<T>()` + constructor injection already solves shared token caches and helpers cleanly without adding another framework concept. If per-run lifecycle hooks or UI visibility for shared services are ever needed later, `IRunService` can be added without breaking plain DI services.
- **Why default plan config:** an Aspire-style load test repo should be ready to run on F5 with presets and environments checked into Git.

## Consequences
- Standalone single-file scripts (brain 0057) and the standalone Beesharp Docker image stay supported for lightweight/container-first workflows; `Beesharp.Host` is the tier-2 path for full C# load-test projects.
- Packaging `Beesharp.Host` requires bundling `Beesharp.Client`'s published static web assets (and updating `.github/workflows/nuget.yml` to publish `Beesharp.Host` alongside `Beesharp.Core`).
- Default plan loading in `PlanWorkspace` also benefits the standalone Docker image (users can set `Plan__DefaultPreset` / `Plan__DefaultEnvironment` in `compose.yaml`).
