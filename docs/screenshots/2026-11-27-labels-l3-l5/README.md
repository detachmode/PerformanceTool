# Labels and readable controls (L2 to L5, brain 0072)

Before = the commit before this work, after = this branch. Fake mode (seeded run) unless noted, 1440 px wide.

## L3a: the run bar says "Start run"
Live mode, preset "Funny jokes" (preflight `check-readiness.cs`). Same bar height idle and running.

| Before | After |
| --- | --- |
| ![](l3a-runbar-preflight-idle-before.png) | ![](l3a-runbar-preflight-idle-after.png) |
| ![](l3a-runbar-preflight-running-before.png) | ![](l3a-runbar-preflight-running-after.png) |

## L3b: the "+" is "+ Preflight"
Live mode, preset "Funny two" (no preflight).

| Before | After |
| --- | --- |
| ![](l3b-runbar-nopreflight-before.png) | ![](l3b-runbar-nopreflight-after.png) |

## L3c: "Failed bee ‹ ›"
| Before | After |
| --- | --- |
| ![](l3c-stepnav-before.png) | ![](l3c-stepnav-after.png) |

The whole scenario page:

| Before | After |
| --- | --- |
| ![](scenario-page-before.png) | ![](scenario-page-after.png) |

## L3d: a step's name opens the logs of that step
The step list on the overview card (names are links now), and the Logs page after clicking "Open product" on the scenario page.

| Before | After |
| --- | --- |
| ![](l3d-card-before.png) | ![](l3d-card-after.png) |

![Logs of the step Open product](l3d-step-click-opens-logs-after.png)

## L4: the bee's Calls tab keeps the end of a call name
| Before | After |
| --- | --- |
| ![](l4-calls-before.png) | ![](l4-calls-after.png) |

## L2: readable faint controls
The header of the Overview, idle and while a run goes (disabled Preset and Edit), then the edit-mode cards ("Click to edit" hint, icons).

| Before | After |
| --- | --- |
| ![](l2-presetbar-idle-before.png) | ![](l2-presetbar-idle-after.png) |
| ![](l2-presetbar-running-before.png) | ![](l2-presetbar-running-after.png) |
| ![](l2-edit-before.png) | ![](l2-edit-after.png) |

## L5: one mode menu
No picture: `document.querySelectorAll('.dd-menu').length` with the mode menu open was 2 before and is 1 after (checked at 1440 px and, through `layout-sizes.mjs`, at 1100, 900 and 390 px).
