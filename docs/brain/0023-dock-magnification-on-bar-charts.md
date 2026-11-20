# 0023. Dock-like magnification on bar charts

- Status: Accepted, implemented (user feature request; updated 2026-11-20: popup only, bar labels, 240 bars)
- Date: 2026-11-20

## What
Like the macOS Dock: when the mouse moves over a bar chart, the bar under the pointer and its neighbours **zoom
in** smoothly, with neighbours scaling down with distance. This makes **small bars easy to see and click**. It
applies to the actor bar charts (`ActorBars`), on cards and in the scenario popup. On touch screens, pressing and
dragging over the chart gives the same effect, and releasing selects the bar.

## Why
With many actors, bars become too thin to hit (user feedback: "hard to click"). Magnification keeps the chart compact
and calm (0016) but makes precise selection easy.

## Consequences
- Bar positions shift under the pointer while magnified, so the hit test must use the magnified layout.
- Keyboard selection keeps working. Users with `prefers-reduced-motion` get the zoom without animation.

## Implementation notes
- `wwwroot/dock-magnify.js` does all per-frame work (`requestAnimationFrame`, inline `flex` widths, `--dz` for a small `scaleY`); `Components/DockZoom.cs` wraps the chart element and loads it. Blazor is called once per selection (`Pick(index)`).
- Falloff: cosine over +-4 bars, hovered bar about 3x wide before compression, bars up to 12% taller, 70 ms easing. Total width is kept fixed by normalising the widths.
- The hit test uses the displayed (magnified) layout. Mouse selects on `click`, touch on release (the following synthetic click is swallowed). Keyboard clicks (`detail` 0) pass through to the bar buttons unchanged.
- `prefers-reduced-motion`: instant change, no easing. Card charts get hover magnification only (a click still opens the card). On touch devices the popup chart is `touch-action: none` and drops its 14px minimum bar width so all bars fit the screen.

## Update (user feedback, 2026-11-20)
- **No magnification on the overview cards.** It only makes sense in the **scenario details popup**, so it is
  removed from the cards.
- In the popup, the **hovered (magnified) bar shows, inside the bar, the actor number and its timestamps**
  (e.g. `#192 · 10:42:05 → 10:42:11`, or start time and duration). Which exact fields is up to the draft;
  keep it short.
- **Less aggressive grouping in the popup (user):** because magnification makes thin bars easy to pick, the popup
  chart shows many more individual actor bars before it groups them into time buckets. The bar count stays bounded
  (0021), with a much higher limit than before.

### Implemented
- Cards: `DockZoom Magnify="false"` keeps only the clock-driven growth of running bars; no magnification, a click still opens the card.
- Popup: a static `span.dz-label` inside the chart is positioned per frame by `dock-magnify.js` over the hovered bar (no Blazor render). It reads `data-l1` / `data-l2` from the bar button: actor `#192` and `start · duration` (or `start · running`), for a time bucket `range` and `N actors`. Times are run time (m:ss), as everywhere else in the tool. Layout: horizontal inside the bar if wide, vertical inside if tall enough, otherwise just above the bar. Dark text on the bar colours. On touch it follows the finger.
- **More individual bars in the popup:** `ActorBars.MaxBars` raised from 48 to **240** (still bounded, brain 0021), because the magnification makes thin bars easy to hit. Beyond 240 actors the chart groups into time buckets as before. Minimum bar width is 4px above 60 bars. Measured with `?simulate=3h` (180 bars): a synthetic mouse sweep ran at a steady 16.7 ms per frame, no frame over 20 ms.
- Screenshots: `docs/screenshots/2026-11-20-popup-zoom-labels/`.

## Update 2 (user feedback, 2026-11-20)
- Improve the look of the hover chart: **cooler labels/panels**.
- **The zoom must be big enough even when the chart is full** (at the bar limit). Magnification should adapt so the
  hovered bar always becomes comfortably wide, whatever the bar count.
- **Neighbouring bars also show info**, in slightly more transparent text, fading with distance.
- **An x-axis under the chart** gives a rough guide to which timeframe the user is looking at and hovering over.

### Implemented (update 2)
- **Hover card** (`.dz-label`, built in JS): status dot (green/yellow/red/blue, with the outcome also in words), bold monospace `#192`, duration, secondary line (`started 2:55`, or failed/slow/running counts for a time bucket). Sits above the hovered bar with a tail pointing at it, fades and slides in; placed inside the bar when the bar is wider than the card. Clamped to the visible chart. No glow, only a soft shadow.
- **Adaptive zoom** (`plan()` in `dock-magnify.js`): the peak factor is solved from the chart width W, the resting pitch r and the falloff radius R so the hovered bar gets about 36-48 px (0.055 W, clamped) at any bar count: `peak*r = target*(W - r*R) / (W - target*R)`, never below 3x. R is 4-8 bars (wider for thin bars), capped so the zoomed area stays below about 65% of W. Measured at 239 bars: hovered bar 48-50 px on desktop (943 px chart), 36 px on a 390 px phone. Rest widths are now fractional (`getBoundingClientRect`), because `offsetWidth` rounding made 1.5 px bars come out wrong. Dense charts (more than 60 bars) use 1 px padding (0 on touch) so thin bars stay visible; the 4 px minimum width (and the horizontal scroll with it) is gone above 60 bars.
- **Neighbour labels**: a strip under the bars shows `#id` plus duration (time bucket: start time plus actor count) for up to 5 neighbours per side, in monospace with opacity fading with distance. Greedy outwards, a label is skipped if it would overlap the previous one, so labels never overlap.
- **X axis** (`.dz-axis`): 3-5 ticks in run time (m:ss / h:mm:ss) at bar positions: the start time of the bar there, the end time at the right edge. Linear in time for time buckets, by bar position for single actors (a rough guide, stated in the chart note). While hovering, a bracket marks the hovered bar and a pill shows its exact timeframe (`2:59.8 -> 3:02.8`, buckets `1:30:00 - 1:31:00`, running `0:22.5 -> now`); ticks under the pill fade out. Ticks follow the magnified layout.
- Per-frame work stays in JS; reads (bar rect, card size) happen before the frame's writes. Reduced motion: no transitions, instant zoom. Touch drag shows the same labels. Click and touch hit-testing use the magnified layout (verified: clicking the widest bar selects it).
- Frame timing, synthetic sweep over 239 bars (3 x 150 moves, 1440x900 headless): rAF interval median 16.7 ms, p95 16.7 ms, max 33.4 ms (1 to 3 frames over 20 ms of about 460); JS time per frame median 4.3 ms, p95 7.7 ms, max 13 ms.
- Screenshots: `docs/screenshots/2026-11-20-chart-polish/`. Debug runs: `?simulate=360s` gives about 239 single-actor bars in the first scenario, `?simulate=3h` the bucketed case.

## Update 3 (user feedback, 2026-11-20)
- **No nested scroll views:** selecting a group of actors (a bucket) must not open a second scrollable list under the
  scrollable popup. Instead, a **list at the side of the popup** shows all actors in the current time frame, sorted
  failed → slow → successful, then by time (0028).
- **Hovered group label:** inside the hovered bar of a group, show how many actors were **successful, failed and slow**
  (nicely designed).
- **Brush to zoom (like Kibana/Elastic):** the user can drag a region on the chart to zoom into that time frame. Works
  well while live data comes in (the zoomed window stays put; a way back to the full/live view).
- **Settings panel:** zoom strength and the max bar count before grouping are adjustable by the user.
- **Live actors while hovering:** actors already running when the chart froze still get their final colour if they
  finish while hovering.
