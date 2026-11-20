# 0029. UI principle: every piece of information drills down

- Status: Accepted (user design principle)
- Date: 2026-11-20
- Sharpens 0006 ("everything clickable and inspectable") into a rule for every number and indicator.

## What
**If the UI shows a piece of information (a counter, a figure, a status), it is clickable and leads to the details
behind it.** Examples:
- an **errors** counter opens the failed actors / their logs;
- a **slow** counter opens the slow actors;
- **running** opens the actors running right now;
- a scenario's numbers on a card open that scenario filtered accordingly.

The drill-down target is the existing view, pre-filtered (popup, side list, Logs popup). No new dead-end views.

## Why
A number raises a question ("which ones?"). The answer should be one click away. Otherwise the number is just noise.

## Consequences
- Every new counter or figure in a brief must say where it drills down to.
- Clickable figures need a visible affordance (hover state, cursor, focus ring) without looking like loud buttons
  (0016).
