# 0023. Dock-like magnification on bar charts

- Status: Accepted, implemented (user feature request)
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
