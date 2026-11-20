# 0027. Run controls as a "player bar" at the bottom, cockpit-inspired

- Status: Accepted (user decision); details via draft
- Date: 2026-11-20

## What
- The run controls move out of the top bar into a **bar at the bottom of the screen, like a music player**:
  **play / pause** (plus stop) and some **details about the run**.
- Look: **cool, like an airplane dashboard / cockpit instruments, but not overdone**. Keep the current calm dark
  look (0016, 0018). Think instrument-style readouts (monospace digits, small labelled gauges/indicators, a progress
  track), not a skeuomorphic cockpit.

Proposed details (to confirm from the draft):
- **Pause** stops starting new actors and pauses the run clock; actors already running finish. **Play** resumes.
  **Stop** ends the run.
- Readouts: elapsed / duration with a progress track, actors running, actors/min (actual / target), errors and slow
  counts (red/yellow only when non-zero), preset name.

## Why
A player bar is a familiar place for "is it running, how far is it, play/pause". It keeps the top bar light and the
run status always in view on every page.
