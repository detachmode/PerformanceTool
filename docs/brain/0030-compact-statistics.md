# 0030. Some statistics come back, compact and coloured by meaning

- Status: Accepted (user request); the exact figures and thresholds are a draft (Proposed below)
- Date: 2026-11-20

## What
- Bring back **some statistics**. They must **not take much space**, must look nice, and their **colour shows whether
  they are good or bad** (0018 meanings). **No boxes around each value.** Find a spot that doesn't waste space.
- Every figure drills down (0029).

**Proposed (draft, to confirm):**
- Figures: **typical duration** (median), **slowest 5 %** (p95), **longest**, **error rate %**, **slow rate %**.
- Placement: inline text, no boxes:
  - in the **scenario popup**, as part of its existing one-line figures (wrapping on phones);
  - on each **overview card**, as one quiet line under the headline.
- Colours:
  - error rate red when > 0;
  - slow rate yellow when > 0;
  - durations yellow when the scenario's slowest 5 % exceeds the slow rule (`SlowCallRule`);
  - otherwise neutral/green.

## Why
Some numbers are needed to judge a run at a glance, but earlier they took too much room as big stat boxes (0012, 0016).

## Notes: overview card step (implemented)
- One inline line under the headline: typical, slowest 5%, longest (hidden on phones), errors %, slow %. No boxes; small faint label, brighter value.
- Colours: errors red when > 0; slow amber when > 0; slowest 5 % and longest amber when above the slow rule (SlowCallRule.IsSlow(value, median), needs 5 finished actors); typical stays neutral.
- Incremental (Services/ScenarioStats.cs): reads only newly finished actors, median/p95 from a sliding window of the latest 200 (refreshed every 5 actors), running maximum for longest. Idle (editor) cards show nothing.
- Drill down: the popup has no pre-filter parameter yet, so a click on a figure opens the scenario popup plainly (the click bubbles to the card, so it opens once). Pre-filtered opening (errors to failed, slow to slow) is still open.
