# 0031. Touch gestures on the popup bar chart

- **Status:** Accepted, implemented (update 2 implemented 2026-11-21)
- **Date:** 2026-11-20
- **Related:** 0023 (Dock magnification), 0026 (stable clicking)

## Context
On a phone, the scenario popup's bar chart behaves like the desktop hover version and feels off. A tap on a
bar to select it also triggers the Dock-like zoom. Holding and dragging to move the magnifier along the bars
doesn't work. There is no way to zoom into a time range, because the desktop's drag-to-zoom (brush) doesn't
work with touch.

## Decision (user)
On touch devices the popup chart uses these gestures:
- **Tap** a bar: select it (highlight, like a click on desktop). No magnification.
- **Tap and hold**: turn on the Dock-like magnification at the finger. **Dragging** while still holding moves the
  magnifier along the bars. Lifting the finger ends it.
- **Swipe** (finger moves before the hold starts): just scrolls the page or popup, even when it starts on the chart.
  No magnification, no selection, no zoom.
- **Pinch**: zoom into or out of the time range (the touch version of the desktop brush zoom).

The desktop mouse behaviour (hover magnification, drag to zoom) stays as it is.

## Consequences
- The chart has to tell touch input apart from mouse input (pointer type) and keep the page from scrolling or
  zooming itself while a hold-drag or pinch is active on the chart.
- Selection by tap follows 0026 (the selection sticks to the actor).

## Implementation notes
- `dock-magnify.js`: touch is a small state machine (wait, scroll, hold, pinch, done) on pointer events. Hold is 400 ms with 9 px tolerance.
  Moving earlier cancels the hold and leaves the swipe to the browser. `touchmove` is `preventDefault`ed (non-passive) only during hold-drag and pinch.
- `.chart` uses `touch-action: pan-x pan-y` (scrolling allowed, browser pinch-zoom off). The old touch brush (hold, then drag) is gone; pinch replaces it.
- Pinch sends the range live (at most every 90 ms) through `DockZoom.OnZoom`; `ScenarioDetailDialog.PinchZoom` clamps it to the run, follows the live edge
  and resets when it covers the whole run. It uses the same zoom window as the brush, so "Reset zoom" works.

## Update 1 (2026-11-21, user)
- **Swipe sideways while zoomed in** moves the zoomed time range left or right (pan along the timeline). It
  stops at the run's start and end. Vertical swipes still scroll the page. When not zoomed, swipes scroll as before.

### Implementation notes (update 1)
- The chart carries `data-zoomed` (and `data-win`, the exact zoom window in run seconds) while zoomed. CSS then sets `touch-action: pan-y`, so the browser keeps vertical scrolling and leaves horizontal moves to the script; unzoomed it stays `pan-x pan-y`.
- `dock-magnify.js`: a new touch mode `pan`. From the first 9 px (SLOP) of movement: mostly horizontal while zoomed starts `pan`, otherwise it is a scroll as before. The range moves with the finger (content follows it) by `dx / chart width * span`, clamped to 0 .. run end. The span stays constant.
- Updates go through the same `OnZoom` path as pinch (throttled to 90 ms, plus once at the end); `PinchZoom` clamps again and follows the live edge when the range reaches the end. No momentum.

## Update 2 (2026-11-21, user)
- **No tap-and-hold** on touch any more.
- **Sideways swipe = magnifier:** any sideways swipe on the chart shows the Dock-like magnification under the
  finger and moves it along the bars (zoomed or not).
- **Edge panning:** when the finger reaches the edge of the visible bars while zoomed in, the view starts to
  pan in that direction (moves the visible time range that way) until the finger moves back or the run's start or end is reached.
  This replaces the map-like pan of update 1.
- **Smooth panning, no jumps:** groups (bars that stand for several actors, 0032) must not be recalculated
  from the view's edges while panning. Build groups on a fixed time grid that does not move with the view, and
  render them a bit beyond the visible edges, so bars slide in smoothly.
- Tap still selects, pinch still zooms, a vertical swipe still scrolls.
- **Release selects (user, same day):** lifting the finger after a sideways swipe with the magnifier selects
  the last highlighted bar (instead of leaving the selection unchanged).
- **Double tap zooms in (user, same day):** a double tap on the chart zooms in on touch devices.
  *Proposed (Claude):* zoom in 2× centred on the tapped spot; the first tap still selects as usual.

### Implementation notes (update 2)
- **Stable grid:** while zoomed (`win` set) `ActorBars.BuildGrid` buckets actors on a time grid whose slot `i` starts at `i * width` from run start (width from a fixed set of nice sizes, `GridWidth(span, maxBars)`, 0.01 s and up). A slot with one actor is a plain actor bar. `ChartGrid` is the sticky buffer: slots from one view-width left to one right of the view, rebuilt only when the width changes or the view is within half a view-width of its edge (not at the run start or the open live end). Unzoomed charts keep the old `Build`.
- **Sliding:** the bars live in `.dz-bars`, sized by `--p` (percent of the chart per slot) and moved by `translateX` from `dock-magnify.js`, which owns the view while the user drives it (edge pan, overview strip) and tells Blazor at most every 90-200 ms. All layout (hover, magnifier, axis, brush) works in chart coordinates = strip coordinates + offset; the magnifier keeps the total width of the visible bars only. A following window now keeps its width and slides with the live edge.
- **Touch:** no hold. A sideways swipe (more than 9 px, more horizontal) is `mag`: magnifier under the finger, release selects the bar under it (not after a pinch or a vertical scroll). A finger within max(32 px, 10 % of the width, up to 80 px) of an edge while zoomed pans (speed ~ closeness^1.6 * 0.7 view-widths per second). `touch-action: pan-y` always (this also stops the browser's double-tap zoom). Double tap (under 350 ms, under 24 px, by event time stamp) zooms 2x around the tap; the first tap selects at once.
- Verified with a published (trimmed) build, Playwright Pixel 7 + CDP touch on `?simulate=3h`: during an edge pan no bucket changed its counts across 189 frames and the absolute position of a fixed bucket moved monotonically, also across a buffer rebuild.
