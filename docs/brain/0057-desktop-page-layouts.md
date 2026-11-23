# 0057. Desktop page layouts: top tabs, cards, the scenario as its own page, editor with a Try column, logs with a filter column

- Status: Accepted (user, from the layout drafts in `docs/screenshots/2026-11-23-layout-drafts/`)
- Date: 2026-11-23

## What
Desktop first; the phone layouts follow from these.
- **App frame: A.** Tabs in the top bar (Overview, Editor, Logs, Environment, then mode, environment, settings); the run controls and figures stay in the player bar at the bottom (brain 0027).
- **Overview: A.** One card per scenario in a grid: name, one headline figure, failed and slow counts (each opens its bees, brain 0029), a small chart, and the step list (brain 0056).
- **One scenario: B.** Its own page with its own address and a way back (breadcrumb Overview › Checkout), replacing the popup (`ScenarioDetailDialog`). Three columns: the steps on the left (they steer the rest: a count filters the page), the chart and the bee list in the middle, the selected bee on the right. While a run goes the steps show "Now" and "Failed" per step.
- **One bee: B.** The selected bee is always shown in the right-hand column of the scenario page; picking another bee swaps it, ‹ › go through the list. No floating panel.

- **Editor: A's toolbar + B's Try column.** One toolbar above the code (file ▾, Saved, Save, Try, ⋯), the code at full height, and the Try result (passed/failed, its steps, then Logs and Calls tabs) in a column on the right. No script list on the left.
- **Logs: B, search on top.** A filter column on the left with a count per filter (level, scenario, steps of the chosen scenario; errors per step), and above the log lines a wide, prominent search field at the top right, the active filters as removable chips to its left.
- **Environment: A.** One centred column: the static values of the selected environment, then the runtime data (run bag, one bag per scenario entry) below.

## Why
Drafts with fake HTML for every page, two or three options each; the user chose these. A page instead of a popup gives the scenario the whole screen, an address to link to and the browser's back button; the bee in a fixed column avoids panels over panels.

## Consequences
- The scenario popup and its floating bee panel go; the overview cards link to the scenario page. Phone layout of the scenario page still to be drafted (tabs Steps · Chart · Bees was the draft).
- Picture of the chosen Editor, Logs and Environment: `docs/screenshots/2026-11-23-layout-drafts/d-chosen.png`.
- Phone layouts are drafted after the desktop is built.
