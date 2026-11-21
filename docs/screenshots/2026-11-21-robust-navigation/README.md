# Popup chart navigation made robust (brain 0040)

Published (trimmed) Release build, Playwright. Phone = Pixel 7 with real CDP touch, desktop = 1280 px at device scale 2.
"Before" is the build before brain 0040, "after" the new one. Scenario entry "Browse categories" (bursts of 15 actors, 6 per minute).

## Overview strip on a 3 hour run (`?simulate=3h`)
Before: the view was a small lilac highlight, nearly invisible far zoomed in. After: an outlined window, the run outside it dimmed,
at least 16 px (touch 24 px) wide, with a pin for the exact spot of a tiny view.

| | Before | After |
|---|---|---|
| Phone, unzoomed | ![](before-phone-3h-01-unzoomed.png) | ![](after-phone-3h-01-unzoomed.png) |
| Phone, zoomed a little | ![](before-phone-3h-02-zoomed-a-little.png) | ![](after-phone-3h-02-zoomed-a-little.png) |
| Phone, zoomed far (about 10 s of 3 h) | ![](before-phone-3h-03-zoomed-far.png) | ![](after-phone-3h-03-burst-unfolded.png) |
| Desktop, unzoomed | ![](before-desktop-3h-01-unzoomed.png) | ![](after-desktop-3h-01-unzoomed.png) |
| Desktop, zoomed a little | ![](before-desktop-3h-02-zoomed-a-little.png) | ![](after-desktop-3h-02-zoomed-a-little.png) |
| Desktop, zoomed far | ![](before-desktop-3h-03-zoomed-far.png) | ![](after-desktop-3h-03-burst-unfolded.png) |

## Bursts unfold as soon as they fit (30 minute seeded run)
Before: a 14 s view still showed each burst as one group bar (it unfolded only at about 1 s). After: on the phone the bursts unfold
at a 28 s view (folded at 56 s), on desktop at 55 s (folded at 79 s). The burst borrows the quiet time around it, so x stays linear in time.

| | |
|---|---|
| Before, phone at 14 s | ![](before-phone-30m-03-zoomed-far.png) |
| After, phone at 56 s (folded) | ![](after-phone-unfold-1-folded.png) |
| After, phone at 28 s (unfolded) | ![](after-phone-unfold-2-at-28s.png) |
| After, desktop at 79 s (folded) | ![](after-desktop-unfold-1-folded.png) |
| After, desktop at 55 s (unfolded) | ![](after-desktop-unfold-2-at-55s.png) |

## Selection pill stays (user decision)
Before: after selecting, the time frame showed only while magnifying. After: the pill stays under the axis, its stem on the lilac mark
(the caret's stem now runs down to it); near the edge it is clamped with the stem still on the bar; outside the view it waits at the
edge with an arrow (tap brings the selection back). Drag it to move the selection, tap it to drill down.

| | |
|---|---|
| Before, phone, selected | ![](before-phone-30m-04-selected.png) |
| After, phone, selected | ![](after-phone-30m-04-selection-pill.png) |
| After, phone, bar near the edge | ![](after-phone-30m-05-pill-at-edge.png) |
| After, phone, selection outside the view | ![](after-phone-30m-06-pill-off-view.png) |
| After, phone, magnifying another bar | ![](after-phone-30m-07-magnifier-with-selection.png) |
| After, desktop, selected | ![](after-desktop-30m-04-selection-pill.png) |
| After, desktop, selection outside the view | ![](after-desktop-30m-06-pill-off-view.png) |
| After, desktop, hovering another bar | ![](after-desktop-30m-07-magnifier-with-selection.png) |

## Live run and whole popup
| | |
|---|---|
| Phone, live run, view at the newest end follows | ![](after-phone-live-following.png) |
| Phone, popup | ![](after-phone-30m-09-dialog.png) |
| Desktop, popup | ![](after-desktop-30m-09-dialog.png) |
