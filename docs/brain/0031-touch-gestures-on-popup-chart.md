# 0031. Touch gestures on the popup bar chart

- **Status:** Accepted (not implemented)
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
