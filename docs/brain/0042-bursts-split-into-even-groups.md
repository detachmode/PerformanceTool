# 0042. A burst that does not fit splits into even groups, not "first few plus the rest"

- **Status:** Accepted, details Proposed, not implemented
- **Date:** 2026-11-21
- **Related:** 0038 (burst unfold, its per-burst cap), 0040 (unfold rule, room borrowed from quiet time), 0032/0037 (group bars), 0021/0022 (bounded charts)

## Context
With a burst of 100 actors, zooming in shows the first ~10 actors as single bars and lumps all the rest into one
big trailing group (0038: failed, slow and running actors first, then the rest as one group). It looks odd and
hides the make-up of most of the burst.

## Decision (user)
- When a burst has more actors than fit as single bars, it splits into **even groups** (e.g. 100 actors → 10 groups
  of 10), not into a few single bars plus one group for the rest.
- Zooming further splits the groups further, until every actor has its own bar.
- **Failed and slow actors stay in their group**; the group turns red or amber as group bars do everywhere else
  (0018/0032). Previous/next failed jumps to that group. This supersedes 0038's "failed/slow/running shown singly first".

## Proposed (Claude, not decided)
- Groups follow actor number (start order), so a group is a contiguous slice of the burst and stays the same while panning.
- Group sizes are "nice" and stable (1, 2, 5, 10, 20, 50, …), chosen from the room the burst has (0040), with the same
  hysteresis, so groups don't flicker at a threshold.
- Each group is an ordinary group bar: count badge, stripes when magnified, clickable, lists its actors (0029).
