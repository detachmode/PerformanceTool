# 0044. The selection marker is one robust piece

- **Status:** Accepted (goal), design Proposed, not implemented
- **Date:** 2026-11-21
- **Related:** 0037 (caret under the selected bar), 0040 (persistent selection pill, user addition), 0043 (group bars)

## Context
The lilac selection marker is still built from separate parts that are placed independently: the 0037 caret (triangle +
stem) under the bar, a lilac mark (dot/bar) on the axis line and the pill with its own stem (0040). In the PR screenshots of
0043 (`docs/screenshots/2026-11-21-burst-even-groups/*-09-red-group-selected.png`) they don't line up: the caret's stem
and the mark/pill stem are a few pixels apart (a kink), the dot sits beside the stem, the caret floats in the gap under
the bar, and the pill is not centred on the marker. On thin bars and on the phone it looks broken.

## Decision (user)
- Fix the lilac pointer **once and for all, make it robust**: it must look like one clean marker pointing at the
  selected bar in every state (single actor, group, unfolded burst, thin and wide bars, near the edges, while panning,
  magnifying, zooming, live run, phone and desktop, any device pixel ratio).

## Proposed (Claude, not decided)
- Render caret, stem and pill as **one element** positioned from **one x value** (the selected bar's centre, snapped to
  device pixels), so the parts can never drift apart; only the pill body clamps inside the chart, the stem never moves.
- Drop the separate axis mark/dot, or make it part of the same element.
