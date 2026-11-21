# 0046. DuckDB columnar storage and run export

- Status: Accepted, implemented
- Date: 2026-11-21

## What
1. **DuckDB as the default `IRunStorage` provider** (`src/PerformanceTool/Services/Storage/DuckDbRunStorage.cs`):
   - Default configuration in `appsettings.json`: `"Storage": { "Provider": "DuckDb", "DuckDbPath": "data/perftool.duckdb" }` (`"Sqlite"` and `"Memory"` remain available).
   - Uses a dedicated background writer draining a `Channel<T>` via DuckDB's native `DuckDBAppender` for bulk columnar ingestion (easily sustaining tens of thousands of log rows/second without blocking actors).
   - Indexed columns on `actors` and `logs`: `run_id`, `sequence`, `actor_name`, `entry`, `is_failed`, `is_slow`, `actor_outcome`, `level_rank`, `level`, `message`, `exception`, `call_index`.
2. **Run export endpoint & UI** (`GET /api/runs/{runId}/export?format=parquet|duckdb|json|csv`, `LogsDialog.razor`, `PlayerBar.razor`):
   - `parquet`: ZSTD-compressed columnar Parquet file of all logs of the run via native DuckDB `COPY ... (FORMAT PARQUET, COMPRESSION ZSTD)`.
   - `duckdb`: standalone `.duckdb` database file containing just that run's `runs`, `actors`, `calls` and `logs` tables.
   - `json`: complete JSON bundle (`run`, `actors`, `calls`, `logs`).
   - `csv`: RFC-4180 CSV of all logs of the run.
   - Exposed via an **Export ▾** menu in `LogsDialog` (all 4 formats) and a quick **Export** button in `PlayerBar` (`.duckdb`).

## Why
- DuckDB is purpose-built for analytical telemetry workloads: columnar compression keeps multi-hour runs compact, `DuckDBAppender` handles massive burst ingestion rates, and native `COPY` / `ATTACH` makes Parquet and single-run `.duckdb` exports trivial and fast for offline analysis (DuckDB CLI, Python/Pandas/Polars, BI tools).
