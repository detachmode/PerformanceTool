# 0031. Touch gestures on the popup bar chart

- **Status:** Accepted, implemented
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
