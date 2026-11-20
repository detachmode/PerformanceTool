# 0008. Live scenario tiles overview, Aspire-dashboard inspired styling

- Status: Accepted, implemented (tiles, detail dialog, restyle; see Implementation notes)
- Date: 2026-11-20

## What
**Scenario tiles.** Each scenario entry is shown as a small box ("tile") with its own **live mini bar chart**
(one bar per execution, newest on the right). During a run, a tile overview shows **all** scenarios side by side:
- **Activity feedback.** A tile **highlights while its scenario is actually executing**: a pulse/glow when a run
  starts, and an indicator for the number of executions in flight right now. The user sees what the tool is
  doing at any moment and how often each scenario fires (target vs actual runs/min, and a "next run in…"
  or rate rhythm cue).
- **Headline numbers.** Each tile shows the scenario label and type chip, a big current value (e.g. p95 or the
  last latency) with a muted unit, error/drop counts as badges, and the sparkline-style bar chart.
- **Click a tile → popup/dialog** with the scenario's details: parameters, full-size chart, latency
  distribution, status codes, error list, and recent executions. Within it you can filter and isolate (0006).
- Tiles also work outside a run (last run's data, or empty placeholders), and they use the scenario's colour (0002).

**The user picked the Aspire 17 Metrics instrument cards as the reference for the tiles.** Match that card closely. **The app stays dark.** Use the dark Aspire look only, even where a reference screenshot shows the light theme.

**Styling, inspired by the Aspire 17 dashboard Metrics page** (instrument cards with live sparklines, see
https://aspire.dev/whats-new/aspire-17/ and https://aspire.dev/dashboard/explore/):
- Flat, neutral, slightly purple-tinted dark surfaces, sampled from the Aspire screenshots: top bar `#0f0e15`,
  page `#221d2d`, cards about `#2a2534`, hairline borders about `#3a3445`, selected row `#303445`. Little to no shadow
  or gradients. Small radius (about 4–6px).
- Tile anatomy: **tag chips** at the top (resource/meter there, scenario type and target here; monospace,
  outlined), a **title** in semibold, a one-line muted **description**, then a **big value** in bold with a
  smaller muted **unit**, then a compact **bar sparkline** across the bottom.
- Grid of equal cards (4 across on wide screens), grouped under a section header with a count and a dotted
  rule. Group by / sort by menus (Aspire offers group by resource/meter/instrument and sort by name/value);
  for us: group by scenario type, sort by name, p95, errors, rate.
- A collapsible left list (Aspire's resource/instrument list with counts), a filter box at the top, and a
  toolbar with icon buttons on the right (view toggle chart/table, sort, columns, clear data, duration selector).
- Chart vs table toggle; dimension filters shown as chips with "+N" overflow popovers (Aspire Metrics filters).
- Status dot + text ("● Running") for live state, and pills such as "p95 9.92ms" / "1117+ incoming" (Aspire resource table).
- Segoe UI–like sans for UI text and monospace for chips/ids. White title text, muted grey-violet secondary text.

## Why
- During a run the user wants an at-a-glance answer to "what is the tool doing right now and how often", per
  scenario. One shared chart answers "how fast" but not "what is active".
- Aspire's card-plus-sparkline layout is a proven, dense, calm dashboard pattern that .NET developers already
  know. Matching its look makes the tool feel at home next to Aspire.

## Consequences
- The categorical palette (0002) was validated against `#131a2e`. Re-run the validator against the new card
  surface before switching, and adjust steps if needed.
- Per-tile charts multiply DOM nodes: cap bars per tile (e.g. the last 60–120 executions) and keep the 150 ms
  render cadence. Activity highlighting must come from runner state (in-flight count per entry), not from
  guessing based on results.
- The runner needs to report "execution started" events and in-flight counts per entry, not only completed results.

## Implementation notes (2026-11-20)
- **Runner.** `LoadRunner.RunAsync(..., onActivity)` raises `ExecutionActivity` (started / ended, in-flight per entry and total).
  Every start has exactly one end (result or cancelled); the start event runs inside the execution task so a slow
  observer never delays the open-model schedule (0004). `Home.razor` counts by event (+1/-1), not by the snapshot,
  because `InvokeAsync` may reorder events of different executions.
- **Highlight.** A tile is "active" while in-flight > 0 or for 0.6 s after the last start: fast requests finish
  between 150 ms renders and would otherwise never light up. The start ping alternates two identical keyframes
  (`ping-a`/`ping-b`) so the CSS animation restarts.
- **Components.** `ScenarioTile` (stats cached per result count, 100 bars max), `ScenarioDetailDialog` (histogram,
  status codes, error groups, parameters via `ParameterSummary` generated from the spec, recent executions; every
  facet is a filter shared by chart and list; Esc/outside click close), `LiveChart` got `Embedded`, `OnBarClick`.
- **Look.** Tokens at the top of `wwwroot/app.css`; dark only, flat, radius 4-6 px.
- **Palette.** Re-validated against card `#2a2534` and page `#221d2d`: all checks pass, steps unchanged. The error red
  `#d03b3b` is 2.57:1 on `#303445` (selected row), so failed bars keep the hatch texture and error text uses `#f2737d`.
- **Not done:** collapsible left list, chart/table view toggle and dimension-filter chips of the Aspire Metrics page;
  only sort and group-by-type menus were added. The dialog has no focus trap.
