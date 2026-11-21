# 0036. Previous / next failed (or slow) works with zoom and groups

- **Status:** Accepted, implemented
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

## Implementation notes
- Why it broke (from reading the code; the old build was not run): `StepTo` only knew the selected *actor* (`selected?.Sequence`) and walked the list in completion order. With grouped bars the selection is a *bucket* (`selected` is null), so every click restarted from the first failed actor; with a zoom, the actor it chose was usually outside the view, so nothing visible changed.
- Now `StepTo` collects the failed (or slow) actors sorted by start time. With groups it steps from the selected group (or the group of the selected actor) to the previous/next group that contains one, wrapping around, and selects the group (a one-actor slot of a zoomed chart selects the actor). Zoomed in and target outside the view: the window moves, keeping its span and centring the target (clamped, follows live at the end); the overview strip follows. The view jumps rather than glides.
