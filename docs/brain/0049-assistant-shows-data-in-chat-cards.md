# 0049. The assistant shows data in small chat cards

- Status: Accepted (principle, user request); card kinds and mechanism Proposed
- Date: 2026-11-21

## What
The assistant (brain 0047) has **show tools**. With them it puts the app's data into the chat as small custom UI
elements (cards) instead of only describing it in text. Example from the user: a little box with the logs it found
and a button to show more, full screen.

Proposed mechanism: a show tool (e.g. `show_logs`, `show_bees`, `show_bee`, `show_scenario`, `show_run`,
`show_plan_changes`) returns a short summary to the model and adds a **view item** to the turn
(`Beesharp.Ai.Contracts`: kind, title, the query behind it such as run id plus log filter, and a small preview). The
client renders it as a card in the chat. The card holds the query, not a copy of everything, so "Show more" opens
the full data: the existing full-screen views where they exist (Logs popup with the filter, scenario popup with
the bee selected, editor), otherwise a full-screen version of the card.

Proposed card kinds:
- **Logs**: a few log lines in the Serilog-style colours, a count ("214 lines"), Show more → Logs popup filtered.
- **Bees**: failed → slow → ok bees with outcome-coloured numbers; a click opens the bee.
- **Bee**: one bee's calls as a small waterfall plus its error; Show more → bee details.
- **Scenario / run**: the mini bar chart and the compact figures of a scenario card or a run; every figure drills down.
- **Plan changes**: what the agent changed in the plan, with a link to the editor.

## Why
The user wants the agent to show data, not only talk about it. Calm UI (brain 0016) and drill-down (brain 0029)
fit: a small preview in the chat, the full data on demand.

## Consequences
- Cards reuse the app's components (`SerilogLine`, `BeeChip`, `BeeBars`, ...) instead of new look-alikes.
- Every counter or figure on a card is clickable (brain 0029). Colour only for failed = red and slow = amber (brain 0018).
- Cards re-query live data when opened, so a card stays useful while a run is still going.
