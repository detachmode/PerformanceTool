# 0052. Environment tab: static values, runtime data, and a shutdown program

- Status: Accepted (goal), details open
- Date: 2026-11-22
- Related: 0033 (environments), 0042 (lifecycle hooks), 0016 (calm UI), 0029 (every info drills down)

## What (user decision)
1. **Environment tab.** A tab (next to Overview, Editor, Logs) where the user **inspects the environment**.
2. An environment has two parts, both shown in the tab:
   - **Static values**: the key-value pairs of the **environment the user selected** (top-bar dropdown, 0033).
     They **never change during a run** (e.g. the base URL). Not loaded by presets (user correction).
   - **Runtime data**: dynamic data that **preflight programs and scenarios can read and write during the run**.
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
- Scope of runtime data: one bag for the whole run (preflight, every scenario entry, shutdown)?

## Consequences
- Current phase is UI drafts with fake data: the tab is first drafted with `FakeRunSource` data.
- Calm UI (0016): show little, reveal on demand. Every figure in the tab drills down (0029).
