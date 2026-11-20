# 0011. Separate pages: lightweight overview + scenario editor

- Status: Accepted (user decision, not implemented)
- Date: 2026-11-20
- Refines 0008. Its single long page (editor, tiles, chart and table stacked) was rejected by the user as "bad".

## What
- **Overview page**: lightweight. Its focus is the **scenario tiles with little box charts per scenario**, like the
  instrument cards on the Aspire 17 Metrics page. Clicking a tile opens the **details popup**.
- **Editor page**: composing the scenario mix (the generated parameter editor from 0007), separate from the overview.
- **The details popup as implemented in 0008 is liked** ("a lot better"). Keep its content and style
  (stats row, full chart, latency distribution, status codes, errors, parameters).

- **User feedback on draft 1:** the rate wasn't prominent enough. The overview must make it very prominent **how many
  instances are running**: live instances running now and instances per minute are the headline of each card
  (and of the run bar). Latency is secondary.

## Why
- The user wants to *watch* runs on a calm, glanceable page. Editing forms on the same page bury the tiles.
- In the 0008 build, the tiles were below a long editor and hard to find, and their mini charts didn't read as the
  little Aspire-style box charts the user asked for.

## Open (to decide with the user)
- Where a run is started and stopped (overview, editor, or both).
- Details of the tile's mini chart (e.g. thin fixed-width bars like Aspire even when there are few executions).
