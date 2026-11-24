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
- **Logs: B with a full-width search.** A filter column on the left with a count per filter (level, scenario, steps of the chosen scenario; errors per step). Above the log lines a full-width search field; under it every active filter as a chip with ✕ to remove it, plus "Clear all", like Kibana / Elastic.
- **Copy all logs (user):** wherever logs are shown (Logs page, a bee's Logs tab, the Try result, preflight and shutdown output, live consoles), a small button copies **all** of them (the whole filtered set, not only the visible lines) to the clipboard.
- **Environment: A.** One centred column: the static values of the selected environment, then the runtime data (run bag, one bag per scenario entry) below.

## Why
Drafts with fake HTML for every page, two or three options each; the user chose these. A page instead of a popup gives the scenario the whole screen, an address to link to and the browser's back button; the bee in a fixed column avoids panels over panels.

## Consequences
- The scenario popup and its floating bee panel go; the overview cards link to the scenario page. Phone layout of the scenario page still to be drafted (tabs Steps · Chart · Bees was the draft).
- Picture of the chosen Editor, Logs and Environment: `docs/screenshots/2026-11-23-layout-drafts/d-chosen.png`.
- Phone layouts are drafted after the desktop is built.

## Implementation notes (Y1: the scenario page)
- **Route and address:** `Pages/ScenarioPage.razor`, `/scenario/{n}` (n counts from 1 like the scenario numbers in the UI). Query parameters (numbers as the UI shows them: step 1 is the first step, bee 7 is B7): `show=failed|slow|slowest`, `failedStep=n`, `nowStep=n`, `bee=n`. `Runs/ScenarioLink` is the one place that maps filter to address and back (tests in `ScenarioPageLogicTests`). The page keeps the list filter, the step filter and a bee picked by hand in its address (history entry replaced, other query parts such as `?stepcount=` stay), so reload, link, back and forward come back to the same view. The bee picked by default (first of the list) is not written to the address. An unknown number shows "No scenario n".
- **Top bar:** `MainLayout` keeps the tabs, marks Overview active on a scenario page and adds the breadcrumb `Overview › name` (the Overview tab is the way back). The overview remembers its scroll position (`wwwroot/scroll-memory.js`, saved when a card opens a scenario page, restored when the overview renders again). Every number on a card (failed, slow, slowest 5%, a step's Failed or Now) and the writers in the Environment page's runtime data navigate to the page with the matching filter.
- **Parts:** `Scenario/ScenarioDetail.razor` holds the three columns and all state (it is the former `ScenarioDetailDialog` without its popup chrome, live feed and floating bee panel; the chart code is unchanged); `Scenario/BeePanel.razor` is the right column (tabs Steps / Logs / Calls, live logs of a bee in flight, copy buttons). `Chart/BeeSideList` is the bee list (number, duration, start, one quiet line from `Runs/BeeLine`); `‹ ›` use `Runs/BeeNavigator`. The first bee of the list is selected when nothing was picked (and again when a filter chip changes); bees in flight come first while a run goes. The popup's "Recent logs / Failed" feed is gone: the failed list is the Failed filter, a bee in flight shows its step in the list, and its logs stream in the bee column.
- **Assistant:** `Assistant/AssistantBeeSetHost` wraps the same `ScenarioDetail` (`Selection`, no left column) in a dialog.
- **Copy all logs:** the bee column's Steps and Logs tabs have a copy button for ALL logs of the bee; each step console (`StepList`, also in the Editor's Try result) has its own for that console. `CopyButton` falls back to a hidden textarea when `navigator.clipboard` is missing (plain http).
- **Layout:** columns 260 px / flexible / 340 to 420 px, each scrolls by itself; below 960 px they stack (left, middle, right) and the page scrolls; picking a bee on a phone scrolls its column into view.
- **Live mode fix found on the way:** bees finish out of sequence order, but the client polled `afterSeq=lastSequence` and so never received bees that finished after a later-numbered one (the scenario's counters and the server's disagreed). `GET /api/runs/current` now also takes `afterIndex` and `runId` (the position in finishing order and the run it belongs to) and answers `nextBeeIndex`; `ApiRunSource` uses them. `afterSeq` still works for other callers.
