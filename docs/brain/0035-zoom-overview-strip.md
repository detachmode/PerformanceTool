# 0035. Overview strip: where am I and how far am I zoomed in

- **Status:** Accepted, implemented (update 1 implemented 2026-11-21)
- **Date:** 2026-11-21
- **Related:** 0023 (popup chart), 0031 (touch zoom and panning), 0016 (calm UI), 0029 (drill-down)

## Context
When the popup chart is zoomed in and panned, you lose track of where in the run you are and how much of it
you are looking at.

## Decision (user)
Add a small **overview strip** to the popup chart: a thin timeline of the whole run with a highlighted area
that shows the part currently in view. Its position shows where you are, its width how far you are zoomed in.

## Proposed (Claude, not decided)
- Show it only while zoomed in (calm UI, 0016).
- Following 0029, it can be used directly: tap or drag the highlighted area to move the view.

## Implementation notes
- `.dz-over` inside the chart (top right, 22 px tall touch target, 6 px track), shown only while `data-zoomed`. `dock-magnify.js` sets the lit part (`.ov-win`) from the live view each time the view moves, so it follows an edge pan at frame rate, and pinch/brush at their update rate.
- Tap on the track centres the view there, dragging the lit part moves it (mouse and touch, pointer capture, view sent to Blazor every 120 ms). Failed/slow ticks in the strip were skipped (calm UI).

## Update 1 (2026-11-21, user)
### Decision (user)
- The strip spans the **whole width of the top section** of the chart (not a small strip at the top right).
- It is **in colour and shows where failed and slow actors were** over the whole run: red = failed, amber = slow (0018), ok stays neutral. This supersedes "failed/slow ticks were skipped (calm UI)".
- **Colours muted so the strip stays quiet** (0016): desaturated / low opacity red and amber, still recognisable, and the lit "in view" area stays clearly readable.

### Proposed (Claude, not decided)
- Drawn as binned marks: the whole run is split into 300 bins; a bin is red if an actor that started in it failed, else amber if one was slow, else nothing. One pass over the actors, a fixed number of marks, so a 3 hour run costs the same as a short one (no mark per actor). Each mark is at least 1.5 px wide so a single failure stays visible.
- **Always visible** (not only while zoomed): now that it carries information it is useful for finding the interesting spots before zooming. When not zoomed there is no lit window.
- Slim: the 22 px touch target stays, the marks are 10 px tall at half opacity.
- Drill-down (0029): tap or drag on the strip when zoomed centres the view there, also on a red or amber spot, as before. When the chart is not zoomed the strip is informational only (the whole run is already in view).

### Implementation notes
- `ActorBars.OverviewMarks` builds a string of `f`, `s` and `.` (300 characters), cached for 0.7 s in the dialog and passed as `data-ov` on the chart. `dock-magnify.js` draws it on a canvas (`.ov-marks`, device-pixel sharp, red over amber) under the lit window and redraws when the data or the width changes. Opacity .5 in CSS.
- `.dz-over` spans the chart's full width (left/right .5rem); the max label ("4.1 s") moved to the right and below it.
- Tests: `OverviewMarksTests`. Screenshots: `docs/screenshots/2026-11-21-burst-unfold/` (`*-02-popup-unzoomed` and `*-03-zoomed-groups`, desktop and phone).
