# 0037. Popup chart: selection marker, swipe areas, striped groups, even gaps

- **Status:** Accepted (not implemented)
- **Date:** 2026-11-21
- **Related:** 0026 (selection), 0031 (touch gestures, supersedes parts of update 2), 0032 (group bars), 0023 (magnification)

## Context
User feedback from a phone screenshot of the zoomed popup chart: the selected bar has a purple block on top
that reads as a strange extra bar; swiping on the bars both magnifies and pans, which is confusing; a
magnified group doesn't show what's inside it; gaps between bars vary.

## Decision (user)
- **Selection marker:** no purple rectangle on top of the selected bar. Instead, a clear marker **below** the
  bar, an arrow (or similar) pointing up at it from the axis area. The selected bar itself may keep a subtle outline.
- **Two swipe areas (touch):**
  - **Axis strip** (the time axis area under the bars): swiping here is the **magnifier** (with edge panning at the
    ends, and release selects, as in 0031 update 2).
  - **Bars area:** a sideways swipe just **scrolls the timeline horizontally** (content follows the finger), no magnifier.
  - Pinch, double tap, tap to select and vertical page scrolling stay.
- **Striped groups when highlighted:** a highlighted (magnified or hovered) group bar shows its make-up as
  **vertical stripes**: red for failed, yellow for slow, green for ok actors, proportional to their counts. Make it nice.
- **Even gaps:** the padding between bars always looks even, at every zoom level and while magnifying.
