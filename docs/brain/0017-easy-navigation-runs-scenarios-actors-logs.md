# 0017. Easy navigation: runs → scenarios → actors → logs

- Status: Accepted (user goal); the exact navigation design is open
- Date: 2026-11-20

## What
It must **feel easy** to:
- navigate the **runs**, their **scenarios** and their **actors**;
- **find the logs**;
- **filter logs by scenario** or **by a single actor**.

This is the main path through the UI. Everything else (statistics, charts) supports it and stays calm (0016).

**Decided: logs get their own popup.** A separate **Logs popup** shows the logs of the whole run (all scenarios)
and filters by scenario and by a single actor (plus level and search). It opens from anywhere (top bar, a scenario,
an actor), pre-filtered to where it was opened from.

## Why
The logs explain what happened. The user wants to get from "something is slow or failing" to the relevant log lines
in a few obvious steps, without hunting through panels.

## Open (to decide with the user)
- How runs are listed and picked (a run history comes with persisted runs, 0009/0012).
