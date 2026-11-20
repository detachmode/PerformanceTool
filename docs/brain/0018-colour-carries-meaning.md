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
