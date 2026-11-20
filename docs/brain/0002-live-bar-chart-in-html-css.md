# 0002. Live bar chart in plain HTML/CSS, one bar per execution

- Status: Accepted
- Date: 2026-11-20

## What
`Components/LiveChart.razor` draws one `<div class="bar">` per scenario execution, in completion order,
height = elapsed time relative to a "nice" Y-axis ceiling. Bars are coloured by **scenario entry** using the
fixed categorical palette in `Services/SeriesPalette.cs`. Failed executions are red **and hatched** (texture,
so failure isn't signalled by colour alone). An average line and per-bar tooltips (`title`) complete it.

## Why
- "One bar per request" was the core requirement. With mixed scenarios the most useful second dimension is
  *which scenario* — speed is already visible as height, so colour encodes identity, not speed.
- Plain HTML/CSS needs no JS chart library or JS interop, so Blazor diffing updates it for free.
- Palette: the 7-slot dark categorical palette, validated (colour-blind ΔE ≥ 8, ≥ 3:1 contrast) against the
  panel surface `#131a2e`. Colours are assigned in fixed order, never cycled, so a plan holds at most
  7 entries (`LoadPlan.MaxEntries`).

## Consequences
- Only the latest 600 bars are rendered (`LiveChart.MaxBars`) to keep the DOM and SignalR diffs small;
  statistics always use all results.
- The page re-renders on a 150 ms ticker, not per result, so fast runs can't flood the circuit.
- Adding an 8th entry means folding into small multiples or another encoding, not inventing a colour.
