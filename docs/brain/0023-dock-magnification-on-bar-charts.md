# 0023. Dock-like magnification on bar charts

- Status: Accepted (user feature request)
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
