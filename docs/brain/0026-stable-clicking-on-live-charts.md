# 0026. Stable clicking on a live chart

- Status: Accepted (user chose options 1–3)
- Date: 2026-11-20

## What
Clicking or tapping a bar on a running chart is hard when actors arrive fast, because new bars push the others along.
The fix combines three things:
1. **Freeze while aiming:** while the pointer is over the chart (or a finger is down), the chart stops moving.
   New actors are held back and counted in a small badge, e.g. "● live paused · 12 new". The chart catches up
   smoothly when the pointer leaves.
2. **Pick on pointer-down:** the bar is chosen from the layout at the moment of pressing, not on release.
3. **Selection sticks to the actor:** once picked, the selected actor stays selected and highlighted as it moves, and
   its calls and logs don't jump.

Not now: time-anchored bar positions (option 4); revisit if the chart still feels restless.

## Why
A target that moves between aiming and clicking makes picking frustrating. Freezing during interaction is a
familiar pattern from live feeds, and keeps the chart live the rest of the time.
