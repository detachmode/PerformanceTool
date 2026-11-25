# 0062. Standalone 3D views page (`/3d`)

- Status: Proposed
- Date: 2026-11-25

## What
Adds a standalone `/3d` page (`Pages/Views3DPage.razor`, `Views3D/Views3DModels.cs`, `Views3D/Views3DProjection.cs`, `wwwroot/views-3d.js`, styles in `wwwroot/app.css`) with three interactive Canvas 2D 3D views backed by real `IRunSource` / `RunStore` and stored-run data:

1. **Scenario & Step Terrain** (`Time × Scenario/Step × Duration`) — reveals cross-scenario and step-level cascades.
2. **Latency Distribution Surface** (`Time × Adaptive Latency Band × Bee Count`) — exposes bimodal latency splits over time.
3. **Run History Compare** (`Time × Previous Runs of Same Scenario × BeeBar.MaxMs`) — compares one scenario across the current run and up to 5 previous runs using `BeeBars.BuildGrid` on a shared X-axis, where each 2D slice matches the scenario chart (longest bee duration per bar/group, outcome color, and dashed end-caps for shorter/manually stopped runs).
4. **Error Cascade** (`Time × Distinct Error Signature × Count`) — orders deduplicated `ErrorDictionary` errors by first occurrence or frequency to expose root-cause chains.
5. **Swarm: Dead or Alive** (`All Scenarios × Steps × Active / Failed`) — live stacked matrix of active in-flight bees (blue → amber) and failures (red) per step, with arbitrary reactive step-to-step arcs (forward/skips/repeats above the floor, backward loops underneath the floor).

All time-bucketed views (`1–4`) share a configurable Grid Resolution slider (`Auto` or `8..120`). Includes offline HTML exploration prototypes (`docs/demos/3d-views-prototype.html`, `docs/demos/3d-ideas-6views.html`, `docs/demos/3d-live-matrix-prototype.html`) and a local Demo Shop target backend (`docs/demos/demo-shop-server.py`, preset `src/Beesharp/presets/demo-shop-3d.json`).

## Why
Single-scenario 2D charts excel at exact inspection, while 3D earns its keep when comparing an extra dimension (across scenarios, across latency bands, or across multiple runs of the same scenario). Keeping it on a separate `/3d` route lets users test it against real scenarios without modifying existing pages. A zero-dependency Canvas 2D renderer keeps the Blazor WebAssembly bundle small, matches Beesharp's flat dark palette and strict green/amber/red meaning (brain 0018), and provides `Isometric`, `Top-Down (2D)`, `Front`, and `Side` presets to eliminate perspective distortion when needed.

## Consequences
- `ApiRunSource` exposes cached read-only `GetRunSummaryAsync` and `GetRunBeesAsync` helpers for comparing stored runs without unloading the current run.
- Styles live in `wwwroot/app.css` (not `.razor.css`, since `index.html` loads only `app.css`), and `views-3d.js` caps DPR/canvas dimensions to avoid mobile GPU/resize-loop crashes.
- Follow-up ideas (e.g. Run-vs-Run Diff Terrain, Burst Staircase, Error Cascade, Event Curtains) are prototyped in `docs/demos/3d-ideas-6views.html` for future evaluation.
