# 0040. Popup chart navigation must be bullet proof: no accidental zoom, earlier burst unfold, readable overview strip

- **Status:** Accepted (problems and goal), design Proposed, not implemented
- **Date:** 2026-11-21
- **Related:** 0031 (touch gestures), 0035 (overview strip, update 1), 0037 (swipe areas), 0038 (burst unfold), 0016 (calm UI), 0029 (drill-down)

## Context (user feedback after 0038 / 0035 update 1)
1. **Accidental zoom:** sometimes a plain swipe left zooms instead of scrolling the timeline.
2. **Bursts unfold too late:** a burst only unfolds when you zoom in very, very far (a view of about 1 s).
   It should unfold as soon as there is enough room to show its actors.
3. **Overview strip:** it is hard to see where the current view is within the run.

## Decision (user)
- Fix all three. The strip (and the navigation around it) must be **bullet proof, scalable and easy to use**:
  think the element through as a whole instead of patching it.
- Bursts unfold **as soon as there is enough space**, not only at the smallest zoom.
- A sideways swipe never zooms.

## Proposed (Claude, not decided)
To be filled in by the design work (see Implementation notes once done).
