# 0032. Group bars look different from single-actor bars

- **Status:** Accepted, implemented
- **Date:** 2026-11-20
- **Related:** 0018 (colour carries meaning), 0023 (Dock magnification, grouping at max bars)

## Context
When a run has more actors than the chart's max bars, one bar stands for a group of actors. It looks the
same as a bar for a single actor, so you can't tell what you are looking at.

## Decision (user)
- **Single actor:** a solid filled bar, as today.
- **Group:** a lighter fill with a solid outline, plus a **small count badge** (how many actors) on top of the bar.
- Colours keep their meaning (0018): the outline and badge use the group's outcome colour.

## Consequences
- Badges must stay small and readable when bars are thin. Where they don't fit, they can show only when
  magnified or hovered (calm UI, 0016).
- The badge drills down like every figure (0029): it opens the actors in that group.

## Implementation
- A group is a time-bucket bar with more than one actor (`IsGroup` in `ScenarioDetailDialog.razor`); a bucket of one actor stays solid.
- CSS (end of `app.css`): `.bar.grp` has a 1 px outline and a 32 % fill in the outcome colour; the badge is a small `<b class="grp-n">` inside the bar, counter-scaled so Dock magnification does not stretch it.
- The badge shows when the bar is at least about 30 px wide (container query), or hovered, selected or magnified (`dock-magnify.js` toggles class `mag` when a bar is more than half magnified). Thin bars show only the outline and lighter fill.
- The badge has `pointer-events: none`, so a click or tap goes to the bar, which already selects the bucket and lists its actors (0029). Touch gestures and brush are untouched. Overview card sparks never group, so they are unchanged.
