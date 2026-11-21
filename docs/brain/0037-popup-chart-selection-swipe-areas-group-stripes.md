# 0037. Popup chart: selection marker, swipe areas, striped groups, even gaps

- **Status:** Accepted, implemented. The axis strip (magnifier) starts 14 px under the bars and the caret got a persistent time-frame pill: see [0040](0040-popup-chart-robust-navigation.md).
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

## Implementation notes
- **Marker:** the purple column fill of the selected bar (`.bar-hit.on` background) is gone; the bar keeps a 1.5 px white outline. `dock-magnify.js` adds a `.dz-sel` caret (accent triangle plus a short stem, `app.css`) to the chart, 3 px below the bars, and places it under the bar with class `on` from the current layout every frame (magnifier), on every pan step (same frame as the strip's transform) and at rest; it glides (200 ms) when the selection changes and hides when the selected bar is out of view. The neighbour label under the selected bar is skipped while magnifying, so it does not cover the caret.
- **Selection survives panning:** `PinchZoom` keeps the selected group when the span is unchanged (a pan of any kind: swipe on the bars, edge pan, overview strip), because the bucket width only depends on the span. Before, any pan cleared it.
- **Swipe areas:** the axis strip is the chart's bottom padding (58 px: neighbour labels plus time axis). A touch that starts there and moves sideways is the magnifier (`mag`, as before: edge pan while zoomed, release selects). A sideways swipe that starts on the bars of a zoomed chart is `pan`: the view moves with the finger by `dx / chart width * span` from the view at touch-down, clamped to 0 .. run end, sent through the same throttled `Zoom` path (120 ms) and followed live at the end by `PinchZoom`; the script owns the view meanwhile (no release while panning). Unzoomed, a sideways swipe on the bars is left to the page. Tap (both areas), double tap, pinch and vertical scroll are unchanged.
- **Stripes:** `ActorBars.Stripes` (tested) orders failed, slow, ok, running, leaves out zeros and gives each a share in percent with a 4 % minimum; the dialog writes it as `data-st="bad:12.5 good:87.5"` on group bars only. The script builds the `.grp-st` stripes lazily inside a group when it becomes magnified (class `mag`, also the hovered bar on desktop): flex segments with 1 px gaps, at least 3 px each, inset 2 px inside the outline, fading in (160 ms); the group's fill turns to the panel colour behind them. The group outline is now an inset box-shadow instead of a border, so thin groups are not forced to 2 px.
- **Even gaps:** browsers round each bar edge on their own, so a 1 px gap at a device scale of 2.625 showed as 2 or 3 device pixels. Now the script sets every bar's width inline (at rest too): cumulative widths rounded to whole device pixels and to the 1/64 px layout unit (so rounding does not add up over hundreds of bars), one gap for the chart (`--gap`, padding on the right of each bar, whole device pixels, from the resting pitch: about 30 % of it, 0.5 to 3.5 px) and the strip offset snapped so the strip starts on a device pixel (this also aligns an unzoomed chart). Measured in the published build: one gap width at every zoom level, while magnifying and after panning (phone and desktop).
- Verified with the published (trimmed) build in Playwright (Pixel 7 + CDP touch, desktop at scale 2) on `?simulate=3h` and a short live run; screenshots in `docs/screenshots/2026-11-21-chart-polish/`.
