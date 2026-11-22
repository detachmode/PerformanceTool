# 0056. The popup's top boxes always show the whole run

- Status: Accepted
- Date: 2026-11-22
- Related: 0030 (compact statistics), 0029 (every info drills down), 0035/0040 (zoom), 0041 (slow)

## What (user decision)
The boxes at the top of the scenario popup (Bees, Errors %, Slow %, Typical, Slowest 5%, Rate) **always show the whole
run**, also when the chart is zoomed. Only the chart and the side list ("Bees in view") follow the zoom.

## Why
They mixed two scopes: the counts were for the whole run, but the percentages, typical and slowest 5% were for the
zoomed time range (`StatsFor(bees, ..., from, to)` in `Chart/ScenarioDetailDialog.razor`). Zoomed into 5 bees it showed
"ERRORS 0%" above 127 errors and "SLOW 20%" above 128 slow bees.
Alternatives the user did not pick: all boxes follow the zoom; whole run plus the view's figure underneath.

## Consequences
- Clicking a box still drills down (0029). A box whose target bee lies outside the zoomed range (e.g. Typical selects the
  median bee) moves the view to it, like previous/next failed.

## Implementation notes (Proposed)
- All boxes, including **Longest** in the selection popup, use one whole-run `BeeStats.Compute(bees, count)`, cached in
  `StatsFor` (recomputed when the finished count changed and at most every 0.7 s, brain 0021), so live runs update
  without flicker. The zoomed statistics are no longer computed; nothing else used them.
- **Typical** (and **Longest**) select the whole run's median (longest) bee; if it lies outside the zoomed range the view
  moves to it, keeping its width (`SelectIndex`, same rule as previous/next failed; a window reaching the live edge follows it).
- **Slowest 5%** applies the whole-run threshold as the tail filter; with a zoom the side list may then be empty when no
  bee in view is that slow (the boxes do not change).
- Tests: `BeeStatsWholeRunTests` (whole-run figures independent of any range; median index outside a zoom).
- Screenshots: `docs/screenshots/2026-11-22-popup-boxes-whole-run/`.
