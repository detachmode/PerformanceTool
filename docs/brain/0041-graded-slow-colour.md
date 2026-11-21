# 0041. Slowness is a colour gradient, from green at typical time to amber

- **Status:** Accepted (gradient from typical), details Proposed, not implemented
- **Date:** 2026-11-21
- **Related:** 0018 (colour carries meaning), `SlowCallRule` (placeholder slow rule: a call over 2× its typical duration), 0028 (failed → slow → ok)

## Context
Slow is one flat amber today. An actor just over the slow line looks the same as one ten times too slow,
and one just under it looks like a perfectly fast one.

## Decision (user)
- The slow colour is **dynamic**: an actor that is only marginally slower than expected shows a **slight greenish
  yellow**; the further it is off its expected (typical) time, the more yellow/amber it gets.
- The gradient **starts at the typical time**: at typical it is pure green, every actor slower than typical gets a tint.
- The hard "slow" line stays for **counting and filters** (Slow counter, Slow filter, failed → slow → ok order).

## Proposed (Claude, not decided)
- Measure per actor: its worst call's duration / that call's typical duration (the same numbers `SlowCallRule` uses).
- Green at 1×, reaching full amber at the slow line (2×), staying amber beyond, so "amber = slow" still holds
  and the slow counter matches the amber bars. Interpolate in a perceptual colour space (OKLCH), few steps so it stays calm.
- Applies wherever an actor's outcome colour is shown: bars, actor numbers in lists, log popup. Failed stays red, running stays as is.
- Group bars and the overview strip: use the worst actor in them (as today for red/amber), or the mean, to be decided.
- Hover/tap on a bar shows the figure, e.g. "1.4× typical" (drill-down, 0029).
