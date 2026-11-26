# 0066. Scenario settings open in a side panel; float parameters get a slider with a fractional step

- Status: Accepted (user feedback), implemented
- Date: 2026-11-26

## What
- In edit mode on the Overview, a scenario's settings (label, spawning, generated parameters) no longer expand inside
  the scenario box. They open in a **side panel**: right of the page on desktop (same fixed-panel pattern as the
  assistant), the existing bottom `Sheet` on the phone (same pattern as the bee on the scenario page, brain 0059).
  The box stays a normal card, marked selected, so the list of scenarios never jumps (brain 0016 calm UI).
- Float, double and decimal parameters (`FieldKind.Number`) render a slider **and** the numeric input beside it
  (typed values may be finer than the step). The slider's range and step come from `SliderScale` (Core):
  `[Range]` min/max when declared, otherwise a window around the current value; step is a power of ten that gives
  about 200 steps over the span (never finer than 0.0001); slid values are rounded to the step's decimals.
  Integers keep whole steps (the former private `SliderBounds`, moved to `SliderScale`).

## Why
- The inline card pushed the other scenarios away and lost the overview while editing.
- A float without a slider could only be typed; sliding is quicker for exploring values.

## Consequences
- No new attribute: `[Range]` already exposes min/max (brain 0007); `SliderScale` is plain Core logic with tests.
- Rejected: a new `[Step]` attribute (not needed yet; add it if a parameter needs a hand-picked step).
