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

## Implementation notes (2026-11-20)
- **Freeze:** `dock-magnify.js` tracks aiming (mouse hover, finger down, keyboard `:focus-visible` inside the chart) and tells Blazor once via `Aim(bool)` (`DockZoom.OnAim`). Leaving or lifting starts a 300 ms grace before `Aim(false)`. `ScenarioDetailDialog` keeps the chart data (bars, scale, bucket width, elapsed, starts) of its last normal render and, while aiming, renders that instead of rebuilding the bars. Running bars still grow (JS clock, frozen `data-elapsed`). Figures and the actor panel stay live. No per-frame Blazor work.
- **Badge:** `● Live paused · N new actors` (N = starts since freezing) sits in a fixed-height strip above the chart, outside it, so it never moves the chart and clicking it does not put the pointer over the chart (which would freeze again). Clicking it resumes at once (`DockZoom.ReleaseAsync`).
- **Catch-up:** before `Aim(false)` the script snapshots bar positions by key; after the re-render the bars glide from old to new place (260 ms FLIP), new bars fade in. Skipped with reduced motion.
- **Pick on pointer-down:** mouse and pen pick on `pointerdown` from the displayed layout; touch picks on release (the chart is frozen while the finger is down); the `click` is swallowed; keyboard unchanged. JS sends the bar's `data-key` (`a<seq>` actor, `r<seq>` running, `b<start>` bucket), not an index; Blazor resolves actors by sequence, so a running actor that finished meanwhile is still picked correctly.
- **Selection sticks:** highlight is by sequence for finished and running bars; an actor folded into a time bucket marks that bucket; a selected running actor finishes in place.
- **Fixed on the way (hit test drift):** a bar cannot be narrower than its padding, so on narrow charts the computed magnified layout was narrower than the drawn one and the hit test drifted (seen on a 390 px phone with 49 bars). Layout widths are now clamped to the bar padding.
- Screenshots and GIF: `docs/screenshots/2026-11-20-stable-click/`.
