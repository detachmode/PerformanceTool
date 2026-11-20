# 0032. Group bars look different from single-actor bars

- **Status:** Accepted (not implemented)
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
