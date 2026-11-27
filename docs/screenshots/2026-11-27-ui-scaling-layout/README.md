# UI scaling and layout (V1 to V4, brain 0068)

Playwright screenshots at deviceScaleFactor 1. V2 was checked with about 100 presets, 100 environments, 100 stored runs, 40 scenario scripts, 30 preflight and 30 shutdown scripts and a 30-scenario plan.

## V1. Failed reads red, slow amber
Bars no longer use lowered opacity (brown and orange on the dark panel) but solid mixes with the panel colour.

| Before | After |
|---|---|
| ![before](v1-overview-failed-before.png) | ![after](v1-overview-failed-after.png) |

## V2. Long lists
Menus scroll inside, header and footer actions stay in reach, a filter shows from 10 entries, the current entry is scrolled into view.

| Preset menu (100 presets) | Filtered |
|---|---|
| ![preset menu](v2-preset-menu-100.png) | ![filtered](v2-preset-menu-filtered.png) |

| Mode menu with stored runs | Environment menu (100) |
|---|---|
| ![mode menu](v2-mode-menu-stored-runs.png) | ![environment menu](v2-environment-menu-100.png) |

| Environments dialog (100) | Editor file menu (40 scripts) |
|---|---|
| ![environments dialog](v2-environments-dialog-100.png) | ![editor file menu](v2-editor-file-menu.png) |

| Add scenario picker | Preflight picker (30 scripts) |
|---|---|
| ![add scenario](v2-add-scenario-picker.png) | ![preflight picker](v2-preflight-picker.png) |

| Logs, scenario filter (30 scenarios) | Overview with 30 cards |
|---|---|
| ![logs](v2-logs-scenario-filter.png) | ![overview](v2-overview-30-cards.png) |

## V3. Layout at every size
The scenario settings panel fills the height (before: black gap of about 75 px, and the grid reflowed from three to two columns when it opened). The column is now kept in edit mode, so the grid is stable.

| Before (1440x900) | After (1440x900) |
|---|---|
| ![before](v3-edit-panel-before-1440x900.png) | ![after](v3-edit-panel-after-1440x900.png) |

| Edit mode, nothing selected | 1920x1000 |
|---|---|
| ![idle](v3-edit-idle-after-1440x900.png) | ![1920](v3-edit-panel-1920x1000.png) |

| 1100x700 | Scenario page 1024x700 |
|---|---|
| ![1100](v3-edit-panel-1100x700.png) | ![1024](v3-scenario-1024x700.png) |

| Top bar 861 wide, before (overflow) | After (compact bar) |
|---|---|
| ![before](v3-top-bar-861x700-before.png) | ![after](v3-top-bar-861x700.png) |

| Player bar 761x700 | Scenario page 960x700 |
|---|---|
| ![player](v3-player-bar-761x700.png) | ![960](v3-scenario-960x700.png) |

`tests/e2e/layout-sizes.mjs` checks all of this at 12 widths and two heights.

## V4. Calls: compact distribution and a detail column
| 1440x900 | 1100x800 |
|---|---|
| ![1440](v4-calls-selected-1440x900.png) | ![1100](v4-calls-selected-1100x800.png) |

| Endpoint, 1440x900 | Endpoint, 1100x800 |
|---|---|
| ![1440](v4-calls-endpoint-1440x900.png) | ![1100](v4-calls-endpoint-1100x800.png) |
