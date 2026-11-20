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
