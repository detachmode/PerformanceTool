# Engine, Scripting and Storage split out of the host (brain 0054, step U2)

Live mode against the server (DuckDB storage). The modules changed; the UI is the same as before.

1. A run starts: the preflight `check-readiness.cs` runs first.
![Preflight running](01-preflight-running.png)

2. The scenarios run (Simulated work).
![Run live](02-run-live.png)

3. The run finished; the shutdown program `cleanup.cs` ran after it.
![Finished with shutdown](03-run1-finished-with-shutdown.png)

4. A second run stopped mid-way: bees still flying were stored as canceled ("Canceled: run stopped"), shown as errors.
![Stopped run](04-run2-stopped-canceled-bees.png)

5. After a page reload the latest run is restored from storage.
![Restored from storage](05-after-reload-restored-from-storage.png)

6. The first run loaded from history (`POST /api/runs/{id}/load`).
![Past run](06-past-run-loaded-from-history.png)

7. Environment tab: final values of the past run (run bag written by preflight, bees and shutdown program).
![Final values](07-environment-final-values.png)

Also driven through REST in the same session: a failing script compile (diagnostic with line and column), script, preflight and shutdown test runs.
