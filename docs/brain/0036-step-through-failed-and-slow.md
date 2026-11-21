# 0036. Previous / next failed (or slow) works with zoom and groups

- **Status:** Accepted (not implemented)
- **Date:** 2026-11-21
- **Related:** 0028 (failed and slow first), 0031 (zoom and panning), 0032 (group bars), 0035 (overview strip)

## Context
The popup's "‹ Previous failed" and "Next failed ›" buttons (also "slow" when the Slow filter is on) stopped
working reliably after zoom, panning and grouping were added.

## Decision (user)
- The buttons work again.
- When the chart is zoomed in and the next or previous failed actor is outside the view, the **view moves** to
  it (the zoom level stays; the overview strip follows).
- They work with **groups**: when bars stand for groups, they step to the next or previous group that contains a
  failed (or slow) actor and select that group.
