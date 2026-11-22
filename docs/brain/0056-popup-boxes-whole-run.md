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
