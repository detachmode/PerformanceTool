# 0073. The run duration slider is not linear: seconds first, then minutes and hours

- Status: Accepted (user request, plan item D), implemented
- Date: 2026-11-27
- Builds on 0016 (calm UI), 0029 (every figure drills down), 0066 (SliderScale), 0071 (the run bar keeps its height), 0068 (DropMenu).

## What
- **Scale** (`Beesharp.Core` `DurationScale`, plain logic, tests in `DurationScaleTests`): the track is a grid of nice values, one slider position per snap step:
  5 s to 1 min in 5 s steps, 1 to 10 min in 30 s, 10 to 60 min in 5 min, 1 to 6 h in 30 min. 50 positions; seconds take about 22 % of the track, up to 10 min 59 %.
  Because a position is one snap step, the arrow keys move exactly one step. `SecondsAt`, `PositionFor` (nearest, lower on a tie), `Snap`, `IsOnGrid`.
- **Value as time** ("45 s", "2 min 30 s", "1 h 30 min"), label "Duration". The field stays typeable: `90`, `90s`, `2m`, `1h 30m`, `2 min 30 s`, `1:30` (m:ss), `1:30:00`
  (`DurationScale.TryParse`, built on `DurationFormat`; unlike it, a plain number is seconds). A value off the grid or beyond the track is kept as typed; the thumb sits at the nearest position / end.
  Unparseable text only marks the field (no message line, so nothing jumps).
- **Marks** under the track at 1 min, 10 min, 1 h and 6 h, small and faint (they sit in padding of the track, so the bar height is unchanged: 65.8 px on desktop idle and running, 0071). The 6 h mark is hidden on a phone.
- **Usual durations**: one small caret next to the value opens a `DropMenu` with 30 s, 1 min, 5 min, 10 min, 30 min, 1 h; a pick sets the value and moves the slider.
- Client component `Shared/DurationSlider.razor`; the plan still stores `LoadPlan.DurationSeconds` in seconds (no format change), Live mode pushes edits as before.

## Why
A linear 10..600 s slider could neither set 5 s nor 2 h. Segment widths trade off: equal width per decade would shrink the seconds; a grid with one position per step keeps keyboard steps meaningful and values always nice.

## Not done
- The track ends at 6 h; longer runs are typed (the thumb stays at the end). No server-side maximum exists.
- Typing `1:30` means 1 min 30 s, not 1 h 30 min.

Screenshots: `docs/screenshots/2026-11-27-duration-slider/`.
