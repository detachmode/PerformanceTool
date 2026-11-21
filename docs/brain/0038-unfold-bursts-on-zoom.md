# 0038. Zooming into a burst unfolds it into single actors

- **Status:** Accepted (details Proposed), not implemented
- **Date:** 2026-11-21
- **Related:** 0034 (bursts), 0031 update 2 (stable time grid), 0032/0037 (group bars), 0016 (calm UI), 0029 (drill-down)

## Context
With bursts (0034), all actors of a burst start at the same time. While zoomed, the popup chart groups actors
into time slots (`ActorBars.BuildGrid`, smallest slot 0.01 s), so a burst stays one group bar however far you
zoom in. Time alone can never split it.

## Decision (user)
Option 1 of four discussed: **unfold a burst**. When a group can no longer be split by time, zooming further
spreads its actors out side by side inside the burst's place on the time axis. Zooming out folds them back into
the group bar. The rest of the chart keeps its time axis.

Rejected: an actor-order axis while zoomed (loses the time between bursts), one column of dots per burst (dense,
hard to tap), jittering start times in the fake data (hides the problem, does not fix the UI).

## Proposed (Claude, not decided)
- A group "can't be split by time" when all its actors share one start time (or the slot is already the
  smallest grid width). Unfolding is triggered by the normal zoom gestures (pinch, double tap, brush, wheel).
- Unfolded actors are ordinary actor bars (solid, clickable, selectable, caret, drill-down as today), ordered by
  actor number, with even gaps (0037).
- A small, quiet label marks the unfolded part, e.g. "burst · 15 actors", so it is clear that this stretch of the
  axis counts actors, not time. The label drills down (0029): it selects the burst's actors as a group.
- The overview strip (0035), previous/next failed (0036) and the time axis keep working; time labels inside an
  unfolded burst all read the burst's start time.

## Consequences
- The x position is no longer purely time inside an unfolded burst; code that maps x to time (brush, magnifier
  labels, overview strip) must treat the burst as one instant.
