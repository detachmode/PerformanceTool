# 0035. Overview strip: where am I and how far am I zoomed in

- **Status:** Accepted (not implemented)
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
