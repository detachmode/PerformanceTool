# 0018. Colour must carry meaning

- Status: Accepted (user principle and scheme direction)
- Date: 2026-11-20
- Questions 0002 (a categorical colour per scenario entry) and the colour choices in 0008/0013.

## What
The current use of colour is not good: colours look **randomly picked for things** (e.g. a different hue per scenario,
tints per call group). From now on, **colour is only used to transport meaning**. Every colour on screen must answer
"what does this tell me?". If it doesn't mean anything, it's neutral.

**Bar colours (user decision, refines the above):** bars are **green for good calls**, **yellow for slow calls** and
**red for failed calls**. Green here means "good" (it carries meaning), so it replaces the neutral default for bars.
Scenarios still have no identity colour.

**Actors in flight (user decision):** actors that are currently being executed are also shown in the bar charts, in
**blue or grey and/or with an animation**, so it's visible what is running right now. The exact style is chosen from a
draft (Proposed: a blue bar that grows with elapsed time and pulses gently, turning green/yellow/red when it finishes).

## Why
Colour is a strong signal. Spent on decoration or arbitrary identity, it adds noise (0016) and hides the signals that
matter.

**Decided by the user:**
- **No separate colour per scenario.** Scenarios are told apart by name, not by colour. This supersedes the colour part of 0002.
- **Colour changes when errors or slow calls occur.** By default things are neutral. Colour appears only to signal a
  problem (errors, slow calls).

## Open (to decide with the user)
- What counts as "slow" (threshold per scenario/call, or relative to the usual duration).
- The exact colours for "error" and "slow".

## Implementation notes (in-flight actors, with 0018)
- Running actors are `RunningActor` records in `RunSnapshot.ActorsInFlight` (per scenario); `ActorBars.WithRunning` adds them as blue bars at the right edge, or counts them into the newest time bucket (a blue segment stacked on the bucket bar).
- Growth is driven by `dock-magnify.js` (one shared rAF loop sets `height` on `[data-start]` bars from the chart's `data-elapsed` / `data-scale`), not by Blazor renders. The pulse is a CSS opacity animation, off under `prefers-reduced-motion`. Colour is `--running` (blue; set a grey to try the grey variant).
- A running actor can be selected; its calls so far show as done (green/red) or running (blue); slow is only known when it finishes. When it finishes, the selection turns into the finished actor in place.

## Update (user, 2026-11-20): card outline
A scenario card's outline is **red** when it has failed actors, **yellow** when it has slow ones (and no failed), and
**green** when it has finished actors and **none** of them failed or were slow. Cards with no finished actors yet
(idle, editor) stay neutral.
