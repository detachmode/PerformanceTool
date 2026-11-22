# 0052. Environment tab: static values, runtime data, and a shutdown program

- Status: Accepted
- Date: 2026-11-22
- Related: 0033 (environments), 0042 (lifecycle hooks), 0016 (calm UI), 0029 (every info drills down)

## What (user decision)
1. **Environment tab.** A tab (next to Overview, Editor, Logs) where the user **inspects the environment**.
2. An environment has two parts, both shown in the tab:
   - **Static values**: the key-value pairs of the **environment the user selected** (top-bar dropdown, 0033).
     They **never change during a run** (e.g. the base URL). Not loaded by presets (user correction).
   - **Runtime data**: dynamic data that **preflight programs and scenarios can read and write during the run**.
   - **Scope:** one **run bag** shared by the whole run (preflight, every scenario entry, shutdown), plus a
     private **entry bag** per scenario entry (today's `context.State`: its startup, bees, teardown). The tab
     shows both, grouped (user answer, 2026-11-22).
   - **A finished run keeps its runtime data as it was at the end** (final values, not every change). A past
     run's Environment tab shows those final values (user answer, 2026-11-22).
3. **Shutdown program**: besides the preflight (runs before the scenarios), there is a shutdown program.
   It **always runs at the end of a run**: after a normal finish, after the user stops the run, and also after a
   failed preflight (so it can clean up what the preflight created) (user answer, 2026-11-22).

## Where the code stands (before this record)
- The active environment is picked in the top-bar dropdown and is **not** part of a preset (0033).
- A preflight is one C# script per plan (`LoadPlan.PreflightScriptId`, `PreflightStore`); it can only **read** the
  environment (`PreflightGlobals.Env`).
- Scenario code has `context.State`, shared only inside **one scenario entry** (startup, its bees, teardown; 0042),
  not with other entries or the preflight. Nothing shows it in the UI.
- There is no shutdown program.

## Open (ask the user, one at a time)
- Nothing yet; the UI draft will raise its own questions.

## Steps (agent queue, docs/plan.md)
- **S1:** Environment tab UI draft with fake data (static values, run bag, entry bags; live and finished runs).
- **S2:** shutdown program (server, plan, player bar / run status), and runtime data wired into the server runner.

## Consequences
- Current phase is UI drafts with fake data: the tab is first drafted with `FakeRunSource` data.
- Calm UI (0016): show little, reveal on demand. Every figure in the tab drills down (0029).

## Implementation notes (S1) - Proposed (Claude, not decided by the user)
- **Page:** `/environment` (`Pages/EnvironmentPage.razor`, one row component `Components/RuntimeRow.razor`), nav item "Environment" after Logs.
  Two sections: Static values (selected environment, "Configure..." opens the existing popup; "No environment" shows a quiet line with the environments to pick) and Runtime data (run bag, then one entry bag per scenario entry).
- **Data seam:** runtime data travels in `RunSnapshot.Runtime` (`RuntimeDataSnapshot`: run bag + entry bags of `RuntimeValue(key, JsonNode, writer, atSeconds)`), so it is read through `IRunSource.Current` like everything else. `null` means the source has none (`ApiRunSource` for now: the page says so quietly). Only the final value per key is kept, no history.
- **Fake data:** `FakeRuntimeScript` (own random source, so the seeded run is unchanged): preflight writes `authToken`, `refreshCount`, `tenantId`, `seedUserIds`, `seedData`; entry startups write `pool`, `completed`; bees write a per-entry value (`cartIds` bounded to 5, `lastOrderId`, ...); a bee of the first entry refreshes the token every ~30 s; teardowns write `cleanedUp` when the run ends (also on Stop).
- **Calm UI:** run bag open, entry bags collapsed; the count ("4 values") is the toggle; a row shows key + compact value on one line, click expands to the full value (pretty JSON for objects) and "Last written by X at m:ss". A value written since the last update fades neutrally for 1.4 s. Credential-looking keys (same rule as the Configure popup) are masked with Show, in both scopes.
- **Drill-down:** writer B42 opens that bee in the scenario popup, Startup/Teardown and the entry bag name open the scenario entry; Preflight/Shutdown navigate to the editor (placeholder until those programs have a view).
- **Filter:** one box over the keys of both scopes; while filtering, matching bags and sections open.

## Implementation notes (S2) - Proposed (Claude, not decided by the user)
- **Model in Core** (`Services/Runtime/RunData.cs`, shared by server and client, JSON wire format): `DataWriter`, `RuntimeValue`, `RuntimeBag`, `RuntimeDataSnapshot` (moved from the client), plus the live side: `DataBag` (thread-safe, last writer and run time per key), `BagAccess` (what code sees: indexer, `TryGetValue`, `Get<T>`, `GetOrAdd`, `TryAdd`, `TryRemove`, enumerable; every write is attributed to the access's writer) and `RunData` (run bag + entry bags, `Version` counter, `Snapshot()`).
- **Code access:** `ScenarioContext.State` (entry bag) is now a `BagAccess` (source compatible for `["k"]`, `TryGetValue`, `GetOrAdd`) and `ScenarioContext.RunBag` is new. Preflight and shutdown scripts get `Data` (the run bag) in their globals. Writers: preflight, startup/bee/teardown of entry N (bee by sequence), shutdown. Run time of a write = the run clock (0 during the preflight).
- **JSON for the UI:** values go through `System.Text.Json`; anything that cannot be serialised (cycles, delegates) shows as its `ToString()`. Values are not size-bounded yet.
- **Shutdown program** = same kind of script as the preflight: `ShutdownStore : PreflightStore` (kind "shutdown", folder `scripts/shutdown/`, same compile/persist/run code), `LoadPlan.ShutdownScriptId`, endpoints `/api/shutdowns` (list, get, upload, delete, test-run) mirroring `/api/preflights`. A test run uses a scratch run bag.
- **Run flow:** `RunManager` runs it in the `finally` of the run task, so it also runs after a stop and after a failed preflight, after the scenarios' teardowns, not cancelled by the stop, with a 120 s limit. Run state `Shutdown` while it runs (the client treats it as active and keeps polling; Stop and Pause are disabled then); `Finished` after it. A failing shutdown sets `ShutdownState = Failed` + error and does not touch the run error or the scenarios' results. Its logs go to the status (`ShutdownLogs`) and to the live logs as "Shutdown". `StopAsync` waits until the scenarios are down, not until the shutdown program is done.
- **Storage:** final values (no history) are saved once after the shutdown program as JSON in a new `run_runtime` table (SQLite, DuckDB) or in memory (`IRunStorage.SaveRuntimeDataAsync/GetRuntimeDataAsync`). Loading a stored run restores them. A run whose preflight failed is not persisted (as before), so its runtime data only lives in memory until the next run.
- **REST:** `GET /api/runs/current/runtime` and `GET /api/runs/{id}/runtime` (live bags for the active run, stored final values otherwise). The status carries `RuntimeVersion` (grows with every write); `ApiRunSource` fetches the snapshot only when it changed and fills `RunSnapshot.Runtime`. Credential-looking keys are sent as they are (the UI masks them, as in S1); no assistant tool exposes runtime data yet (follow-up: add one with the assistant's secret masking).
- **UI:** shutdown row next to the preflight row in the plan editor (same picker, `Add shutdown`, `+ Create new shutdown .cs`), `ShutdownStrip` above the scenario cards (running or failed: output open; finished: one quiet line, output on demand), player bar says "Shutting down", script editor lists shutdown programs (`?shutdown=id|new`, tag END), writer "Preflight" / "Shutdown" on the Environment tab opens `ProgramDialog` with the output (0029). `RunSnapshot.Shutdown` / `.Preflight` (`ProgramRun`) carry it for both data sources.
- **Fake mode:** the fake plan has a shutdown program `cleanup.cs`; at the end (also after Stop) it writes `deletedCarts`, `cleanedUp` and `tenantId` into the run bag as writer Shutdown and logs lines. It is instant (no "shutting down" phase in Fake).
- **Samples:** `check-readiness.cs` writes `tenantId`, `authToken`, `preflightAt`; `shutdown/cleanup.cs` releases the tenant; `simulated-work.cs` bees write `lastWork` to the run bag.
- **Not done:** preflight/shutdown logs are not stored with the run (only the live ring buffer), assistant tools and plan-change summaries do not know `ShutdownScriptId` yet.

