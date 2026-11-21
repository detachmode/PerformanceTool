# 0040. Popup chart navigation must be bullet proof: no accidental zoom, earlier burst unfold, readable overview strip

- **Status:** Accepted (problems, goal, selection pill), design Proposed, implemented
- **Date:** 2026-11-21
- **Related:** 0031 (touch gestures), 0035 (overview strip, update 1), 0037 (swipe areas), 0038 (burst unfold), 0016 (calm UI), 0029 (drill-down).
  Supersedes parts of 0031 (gesture details), 0035 (strip look and handling), 0037 (swipe area boundary), 0038 (unfold trigger, width, x mapping); noted there.

## Context (user feedback after 0038 / 0035 update 1)
1. **Accidental zoom:** sometimes a plain swipe left zooms instead of scrolling the timeline.
2. **Bursts unfold too late:** a burst only unfolds when you zoom in very, very far (a view of about 1 s).
   It should unfold as soon as there is enough room to show its actors.
3. **Overview strip:** it is hard to see where the current view is within the run.

## Decision (user)
- Fix all three. The strip (and the navigation around it) must be **bullet proof, scalable and easy to use**:
  think the element through as a whole instead of patching it.
- Bursts unfold **as soon as there is enough space**, not only at the smallest zoom.
- A sideways swipe never zooms.
- Claude may also change the popup chart's UI/UX beyond the three points where clearly better (calm UI, drill-down, colour
  meaning, bounded, domain language still hold); each change is listed below as Proposed.
- **Addition (user, phone screenshot):** after a bar is selected, its **time-frame pill under the axis stays visible** until the
  selection is cleared, and its **stem stays attached to the lilac mark of the bar** while panning, magnifying, zooming and near the
  edges (pill clamped inside the chart). It should look and behave grabbable.

## User idea (to evaluate): compressed time gaps
Show long quiet stretches between actors as a gap block labelled with the time it stands for ("+8.2 s"), its width between a minimum
and a maximum. **Verdict (Claude): not now.** The unfold rule below already gives bursts room at a 28 s (phone) / 55 s (desktop) view
by borrowing the quiet time around them, and keeps x linear in time, which every part of the navigation relies on: with compressed
gaps pan speed would change under the finger, the strip window would no longer match the chart, ticks/brush/magnifier need per-block
maths, the stable grid (0031) would become blocks that appear and vanish while zooming (layout jumps), and the gap at the live edge
keeps growing. Linear spawning has no gaps (no gain); bursts have regular gaps, so the chart would fill with "+8.2 s" labels (calm UI).
Worth revisiting only as an explicit "skip quiet time" switch for sparse runs.

## Proposed (Claude, not decided)
**Why a swipe zoomed (all reproduced in Playwright on the published build before fixing, Pixel 7 + CDP touch):**
- A finger that lands while a swipe is going on (thumb of the holding hand) turned the swipe into a pinch (span 56 s → 142 s).
- Two fingers swiping sideways together: their distance jitters, and touch events arrive one finger at a time, so the first event looked like a spread (56 → 154 s).
- A touch whose end never reached the chart (lifted outside it after its bar was re-rendered away) stayed in the pointer list: from then on **every** one-finger swipe was a pinch until the popup closed (38 s → 500 s). This is the "sometimes".
- A fast flick whose moves were not delivered counted as a tap (the lift position was not checked); two flicks = double tap = zoom. Two short 8 px nudges also made a double tap.
- A swipe aimed at the short bars often starts just under them, in the axis strip, and starts the Dock magnifier (looks like zoom).
- Desktop: a sideways trackpad swipe did nothing; a trackpad pinch zoomed the whole browser page. And 0038's "burst widens the chart" made a pan across a burst change the visible time range (looked like zoom).

**Gesture rules (fixes):** the gesture is decided once (a finger landing after a swipe started is ignored; within 150 ms of a pan it may still become two-finger);
two fingers zoom only after their distance changed by 15 % (decided once both reported a move), moved together they scroll the timeline and stay a scroll;
a tap must lift within 9 px and 600 ms, a double tap needs two still taps (under 5 px); a flick that lifted far away scrolls; pointers are forgotten wherever
they end and a new first finger drops stale ones. The magnifier strip starts 14 px under the bars. *Reason: no path from a sideways move to a span change.*
- **Wheel:** sideways wheel / trackpad swipe / Shift+wheel scrolls a zoomed timeline; Ctrl+wheel (trackpad pinch) zooms the chart around the pointer instead of the page. Plain vertical wheel scrolls the page. *Reason: the desktop "swipe".*

**Unfold rule:** a burst may borrow the quiet time around it (at most 35 % of the gap to its nearest neighbour on each side, so the gap stays visible)
and unfolds when its actors get at least `max(4 px, 1.5 × chart width / max bars)` each in that room; it takes only as much room as bars of up to 48 px need.
Its bars share the borrowed slots, so **x is linear in time everywhere** (fixes 0038's right-edge/pan-slip limitation; the view's end is the chart's edge).
Hysteresis as play in the zoom: zooming out keeps the decision until the view is 1.4× wider (bars ~30 % narrower). The decision depends on zoom and data,
not on the part in view: panning never folds or unfolds. Bounded by construction (all unfolded bars fit the chart at the minimum width) plus 0038's caps.
Seeded run (15 actors every 10 s): unfolds at a 28 s view on a phone, 55 s on desktop (was ~1 s).

**Overview strip (redesigned):** a 12 px track with the muted marks (0035 update 1); the view is an **outlined window** (light 1.5 px outline, dark halo)
and the run **outside it is dimmed**. The window is at least 16 px (touch 24 px) wide, a thin pin marks the exact spot of a tiny view (3 h run, 10 s view).
Touch target 32 px on touch screens, 26 px with a mouse. *Reason: readable at every scale, findable and grabbable.*
- Drag the window (or anywhere on the strip, like a scrollbar) to move the view; tap/click the strip to centre the view there. On touch a vertical swipe on the strip still scrolls the page.
- Unzoomed: tap a spot to zoom into an eighth of the run around it (drill-down, 0029: e.g. tap a red spot).
- Keyboard: the strip is a slider (←/→ a quarter view, PageUp/PageDown a view, Home/End), with the view as its value text.
- Live: a window at the newest end follows the run and stays at the strip's right end; the strip rescales as the run grows.
- Rejected: **dragging the window edges to zoom** (a sideways drag must never zoom; the edges of a 16-24 px window cannot be told apart by a finger; pinch, double tap, brush, Ctrl+wheel already zoom); a brush on the strip; lighting the inside (low contrast on amber marks).

**Selection pill (user decision above; details Proposed):** the pill sits under the axis with its stem on the lilac mark; the 0037 caret's stem now runs
down to that mark, so caret, mark and pill read as one marker. Near the edges the pill is clamped and the stem slides inside it to stay on the bar.
- **Grab it:** drag sideways to move the selection along the bars (the magnifier follows, edge pan while zoomed, lifting selects).
- **Click/tap it (drill-down):** a group zooms into its time frame; an actor scrolls its calls panel into view (brief outline).
- **Selection outside the view:** the pill waits at that edge with an arrow (`‹ 15:19.4 → 15:21.3`, dashed); a click brings the selection back into view (same zoom).
- While the hover pill of another bar would cover it, it steps back (the caret and mark stay); otherwise it stays, dimmed.

**Small layout changes:** the chart's top band is a little taller (room for the strip); the max-duration label has the panel colour behind it so a burst label cannot cover it.

## Implementation notes
- `ActorBars.Unfold(…, UnfoldRoom room, …)`: one pass over the actors finds, per candidate group, its actors and its nearest occupied slots (binary search over the
  candidate slots); `UnfoldRoom.For(chartPx, span, decideSpan, width, maxBars)`, `ActorBars.MinActorPx`, `GapShare`, `MaxActorPx`; `UnfoldHysteresis` (play 1.4).
  `ActorBar.Cover` = slots a bar covers; the dialog writes it as `data-k` and `--k` (CSS `flex-basis: calc(var(--p) * var(--k))`). The script's `slotAt/slotW`
  replace 0038's cells: `xOfTime` is linear, `timeAt` maps by slots. `DockZoom.OnSize` reports the chart width (`Size` from the script's ResizeObserver).
- `dock-magnify.js`: touch modes `two` / `pinch` / `pan2` (two fingers), ignored late fingers (`ign`), `onLostEnd` on window, tap/flick checks, `AXIS_GRACE`,
  `onWheel`, strip (`ov-dim`, `ov-win` + grips, `ov-pin`, `ovGeom`, pointer and key handlers), selection pill (`dz-readout.sel` button, `dz-mark.sel`, `pillClick`, scrub).
- Tests: `BurstUnfoldTests` (room, bounded, linear x, pan independence, hysteresis, earlier unfold). Playwright on the published trimmed build (Pixel 7 + CDP touch,
  desktop at scale 2, 30 min seeded run and `?simulate=3h`): every repro above keeps the span; 40 quick swipes (and 40 sideways wheel swipes) keep the span and
  the unfold state; pinch, double tap, brush, Ctrl+wheel, strip drag/tap/keys and the pill still work; a live view at the newest end follows.
- Screenshots (before/after): `docs/screenshots/2026-11-21-robust-navigation/`.
