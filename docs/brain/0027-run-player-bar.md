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

## Implementation notes
- `Layout/PlayerBar.razor` (fixed bottom, z-index 60) in `MainLayout`; the top bar keeps brand, nav and build stamp. CSS block "player bar" at the end of `app.css`; `.main` has bottom padding.
- **Logs button lives in the bar** (right end): it is a run detail, and the top bar stays light (0016). On the phone it is revealed with the details toggle.
- `FakeRunState.Paused`, `IRunSource.Pause()/Resume()` (the API stub throws). `RunSnapshot.Running` means "run active" (running or paused); `Paused` is separate. While paused the run clock stands still, nothing new starts, and in-flight actors finish on a side clock. The editor stays locked while paused.
- Readouts: preset, elapsed / duration with a ticked progress track, running (blue), actors/min actual / target, errors (red if > 0), slow actors (yellow if > 0), a state lamp (idle grey, running blue pulsing, paused white, finished green).
- Phone (<= 760px): play, stop, elapsed with track, running, errors and a details toggle; actors/min, slow, state text and Logs appear when it is open.
- Space toggles play/pause (`wwwroot/player-bar.js`) unless focus is in an input, button, link or similar. A visually hidden polite status announces only state changes; readouts are not live. `prefers-reduced-motion` stops the lamp pulse and the progress transition.
- The fake source starts as a finished pre-seeded run, so "Idle" is not visible on load; the first screenshots show the Finished state.
