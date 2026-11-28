# 0074. Compiled scenario source inspection and per-run source snapshots

- Status: Accepted, implemented
- Date: 2026-11-28

## What
- **Portable PDB source resolution** (`Beesharp.Scripting/CompiledSourceResolver.cs`): resolves original C# source files for compiled `IScenario` and `IRunProgram` classes using `System.Reflection.Metadata` (zero new NuGet packages). Reads embedded Portable PDB sources (`<EmbedAllSources>true</EmbedAllSources>`) first, falling back to local disk paths recorded in the PDB, inspecting nested compiler-generated async state machines and separate parameter types.
- `Beesharp.Core.csproj` sets `<EmbedAllSources>true</EmbedAllSources>` (in the `.pdb`, keeping `Beesharp.Core.dll` and the trimmed WASM bundle unchanged); `Beesharp.Host.targets` defaults `<EmbedAllSources>true</EmbedAllSources>` for host consumers (opt-outable).
- `GET /api/scenarios/{id}/source` returns `CompiledScenarioSourceDto`; `ScriptProgramStore` resolves real source for compiled preflights/shutdowns and marks `PreflightScriptSummary.IsCompiled = true`.
- **Per-run source snapshots** (DuckDB schema v7): `RunManager` captures the source of every scenario, preflight, and shutdown in a run's `LoadPlan` at run start (`IRunStorage.SaveRunSourcesAsync` / `GetRunSourcesAsync`). DuckDB deduplicates code by UTF-8 SHA-256 in `source_blobs(sha256, code)` linked from `run_sources(run_id, kind, target_id, ...)`.
- `GET /api/runs/{runId}/sources` returns `RunSourceSnapshotDto` entries annotated with `DiffersFromCurrent` by comparing stored SHA-256 against the currently loaded code.
- **Editor UI**: `CSharpCodeEditor` / `monaco-host.js` gain read-only mode; `Editor.razor` lists compiled scenarios (with `▶ Try` and `+ Add to plan`) and active/requested run snapshots (`?run={runId}`, with drift badge and "Open current").
- `examples/tracing/AppHost` now launches `examples/tracing/beesharp-host/Beesharp.Tracing.Host.csproj` using the `Beesharp.Host` compiled-scenario pattern.

## Why
- Compiled `Beesharp.Host` scenarios are real C# classes, so operators inspecting a deployment or demo couldn't see what a scenario actually did from `/editor`.
- Portable PDBs + `EmbedAllSources` are built into the .NET SDK, need no Roslyn analyzer project, keep comments and C# 15 `union` syntax intact (unlike decompilation), and guarantee the embedded source matches the compiled binary even when local files are later edited.
- History runs need a per-run snapshot because live scripts and recompiled binaries drift over time; content-addressing by SHA-256 keeps storage overhead near zero across repeated runs.

## Consequences
- Keeping `CompiledSourceResolver` in `Beesharp.Scripting` (not `Beesharp.Core`) avoids pulling `System.Reflection.Metadata` into the trimmed Blazor WASM bundle.
- Older DuckDB databases (schema <= 6) upgrade in place automatically with `CREATE TABLE IF NOT EXISTS`; runs recorded before schema 7 simply return an empty snapshot list.
