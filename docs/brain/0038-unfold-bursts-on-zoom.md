# 0038. Zooming into a burst unfolds it into single actors

<<<<<<< HEAD
- **Status:** Accepted (details Proposed), not implemented
=======
- **Status:** Accepted, implemented (details were Proposed; deviations listed in the implementation notes)
>>>>>>> worktree-agent-a0d17b4a582d3828d
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
<<<<<<< HEAD
=======

## Implementation notes
Screenshots: `docs/screenshots/2026-11-21-burst-unfold/` (desktop at scale 2 and Pixel 7, published trimmed build, 30 minute seeded run).

- **Where it happens.** `ActorBars.Unfold` is a post-pass over the grid's slot bars (the buffer `ChartGrid`/`BuildGrid` build is unchanged, so running actors, aiming freeze and the script's idea of "slot i" stay valid). The dialog applies it at render time to `cd.Bars`.
- **Trigger (Proposed, kept, made concrete).** Only in a zoomed view. Bursts unfold when at most `UnfoldShare` (40 %) of the max-bars setting is in view as grid slots (96 slots at the default 240). Because the grid width is chosen so that 96 to 240 slots are in view, this is in practice the smallest grid width (0.01 s, views of about 1 s or less), which is what a click on a group bar and "Zoom in", a brush over one group bar, or a few double taps give. The minimum pinch/double-tap span in the script went from 0.5 s to 0.05 s so you can zoom that far.
- **What unfolds.** A slot bar of at least two actors whose actors all start within 1 ms of each other (or any such group in a slot of the smallest grid width, which time cannot split further). Actors (running ones too) become ordinary single-actor bars, ordered by start and actor number, between the neighbouring slots.
- **x is not time inside a burst (deviation from "inside the burst's place": the burst widens, it does not squeeze into one slot).** A slot is as wide as one bar (`--p`), a burst needs one bar per actor, so the burst takes extra width and the bars after it move right. Pan/zoom views still start at the view's left edge: the right edge of the chart now shows slightly less than the view's end when a burst is in view (the side list, statistics and the overview strip still use the whole view). The script groups the bars of one burst into a cell (`data-ub`); `timeAt` and the offset of the strip map time to x by cell, so ticks, the magnifier, double tap, pinch and the strip offset treat the burst as one instant. Brush over bars of a burst gives the burst's slot (`data-ts`, `data-te`). Panning by a finger or the overview strip moves time linearly (the finger slips a little over a burst).
- **Width of unfolded bars.** `min(slot width, 3rem)`, so a one-slot view does not give 15 bars of full chart width.
- **Label.** A quiet `burst · N actors` button (`.burst-tag`) in the top band over the stretch, thin underline, accent when its group is selected; the script positions it from the current layout (magnifier and pan included). Mouse click, tap or keyboard select the burst as a group (key `b<slot>`, same as a group bar), the actors of the burst show a faint background (`.bar-hit.inb`) and the side list is narrowed to them.
- **Bounded (decided here).** A burst shows at most `max(12, maxBars/4)` bars (60); if it has more actors, the most interesting (failed, slow, running) are shown singly and the rest are one trailing group bar (count badge, selects the whole burst); the label always says the full count. All unfolded bursts together add at most `max(24, maxBars)` extra bars; the bursts nearest to the view's centre unfold first. 500 actors: 59 bars + 1 group bar. Tests: `BurstUnfoldTests`.
- **Previous/next failed (0036).** While a burst is unfolded it steps actor by actor inside it (key = slot, actor number); otherwise it steps by group as before.
- **Not changed:** the overview strip and Show filters work on the actors as before; with Failed/Slow filters a burst unfolds only if two or more matching actors share the slot.
>>>>>>> worktree-agent-a0d17b4a582d3828d
