# 0041. Slowness is a colour gradient, from green at typical time to amber

- **Status:** Accepted, implemented (details Proposed)
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

## Implementation notes (details are Proposed, not decided by the user)
- **Ratio:** `SlowCallRule.Ratio` = call duration / its typical (1 when not slower or no typical yet). An actor keeps the ratio of its **worst call**
  in one byte (`ActorRecord.RatioCode`, 1/32 steps up to about 8.9x, rounded down so nothing under the slow line reads as slow). `RunStore` keeps the
  same byte per sequence (`TintOf`, `RatioOf`) for the Logs popup. `ActorRecord` stays tiny (one byte more).
- **Colour:** `SlowCallRule.Step(ratio)`: 0 at typical (pure green), steps 1 to 3 evenly up to the slow line (every ratio over 1 gets at least step 1),
  step 4 (full amber) from 2x. A slow actor (some call over the slow line) is always step 4, so amber still means slow and the Slow counter matches the amber.
  Few steps keep it calm. In CSS `--g1..--g3` are `color-mix(in oklch, var(--slow) 25/50/75%, var(--good))`; classes `t1..t3` only override
  the element's own `--good`, so every green thing (bars, group outlines, stripes, chips, sparks, call rows) picks it up. `ActorOrder.Good(step)` gives the class.
- **Where:** card sparks, chart bars, ok stripe of a group, actor numbers in the side list, selected actor, Logs popup, call rows. Failed stays red, running blue.
- **Groups (Proposed):** a group bar takes its **worst** ok actor (max ratio), not the mean. Same rule as red and amber ("any actor"), a mean would hide the one
  actor that is off, which is what a bar is for. The ok stripe of a magnified group takes that step too, unless slow actors are in the group (then it stays plain green).
- **Overview strip (Proposed):** unchanged: it marks only failed and slow actors, muted (0035 update 1). The gradient lives below the slow line, where the strip draws nothing,
  and every slow actor would be full amber anyway. Marking tinted ok actors would make the strip busy.
- **Drill down (0029):** hover/title of a bar says "1.3x typical" (for a group "the slowest 1.3x typical"). The selected actor panel has a "1.3x typical"
  button (shown from 1.05x, below that the tint is faint) that toggles the typical time and ratio under every call; call rows are tinted by their own ratio.
- **Fake data:** the existing lognormal spread of call durations already gives ratios from just over 1 to the slow tail (tail calls 2.5x to 6x).
- **Deviation:** none from the Proposed list. Tests: `GradedSlowTests`.
