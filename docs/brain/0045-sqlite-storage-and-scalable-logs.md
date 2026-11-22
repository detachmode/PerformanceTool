# 0045. SQLite WAL storage and scalable lazy-loaded logs

- Status: Accepted, implemented; the SQLite provider was removed by 0054 (U2), the lazy-loaded logs and bee details remain
- Date: 2026-11-21

## What
1. **Pluggable `IRunStorage` with SQLite WAL default** (`src/PerformanceTool/Services/Storage/IRunStorage.cs`, `SqliteRunStorage.cs`, `InMemoryRunStorage.cs`):
   - Selected via `"Storage": { "Provider": "Sqlite" | "Memory", "SqlitePath": "data/perftool.db" }`.
   - SQLite runs in `WAL` mode (`synchronous = NORMAL`, `temp_store = MEMORY`, 32 MB cache) with a dedicated background writer draining an unbounded `Channel<T>` in batched transactions (up to 2,000 items or 100 ms per commit) using prepared `SqliteCommand`s.
   - Normalized tables (`runs`, `actors`, `calls`, `logs`) with composite indexes on `(run_id, id)`, `(run_id, entry, id)`, `(run_id, sequence, id)`, `(run_id, actor_name, id)`, `(run_id, outcome, id)`, `(run_id, is_failed, id)`, `(run_id, is_slow, id)`, and `(run_id, level_rank, id)`.
2. **Scalable API & incremental polling** (`RunManager.cs`, `Program.cs`):
   - `GET /api/runs/current?afterSeq={n}` returns only compact new actor records (no embedded calls or logs).
   - `GET /api/runs` lists persisted runs.
   - `GET /api/runs/{runId}/actors/{sequence}` lazily fetches one actor's full calls and logs on click.
   - `GET /api/runs/{runId}/logs?entry=&actor=&actorName=&outcome=&failed=&slow=&levels=&search=&offset=0&limit=200` runs indexed server-side filtered & paged queries.
3. **Client virtualization + lazy loading** (`ApiRunSource.cs`, `VirtualList.razor`, `LogsDialog.razor`, `ScenarioDetailDialog.razor`):
   - `LogsDialog` in Live mode queries `/api/runs/{runId}/logs` in 250-row pages and lazy-loads more pages via `VirtualList.OnNearEnd` as the user scrolls.
   - `ScenarioDetailDialog` lazily fetches actor calls/logs on demand when an actor bar is selected.

## Why
- Embedding all calls and logs inside `/api/runs/current` does not scale to thousands of logs/second or multi-hour runs.
- Writing synchronously per actor to disk would stall `LoadRunner` threads; batching through a `Channel<T>` into WAL-mode SQLite transactions sustains tens of thousands of log inserts/second while keeping concurrent indexed reads fast.

## Consequences
- `Microsoft.Data.Sqlite` lives only in the server project (`PerformanceTool`); `PerformanceTool.Core` stays dependency-free.
- `src/PerformanceTool/data/` is git-ignored so local run databases are never committed.
