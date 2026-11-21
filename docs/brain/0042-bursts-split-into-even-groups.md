# 0042. A burst that does not fit splits into even groups, not "first few plus the rest"

- **Status:** Accepted, implemented (details were Proposed; deviations and choices in the implementation notes)
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

## Implementation notes
Screenshots: `docs/screenshots/2026-11-21-burst-even-groups/` (published trimmed build, debug URL `?burst=100`, desktop at scale 2 and Pixel 7).

- **Rule (`ActorBars.Unfold`).** Per burst: `fit` = how many bars of at least `MinActorPx` fit the room the burst may borrow (decided with the hysteresis width of 0040, capped by the per-burst cap and what is left of the budget). `ActorBars.NiceGroupSize(actors, fit)` picks the smallest size from 1, 2, 5, 10, 20, 50, 100, ... with at most `fit` groups. Fewer than two groups fit: the burst stays one folded group bar. 100 actors: 2 x 50, 5 x 20, 10 x 10, 20 x 5, 50 x 2, then 100 single bars as the room grows. The last group of a burst may be smaller (130 actors in groups of 50: 50, 50, 30); a group of one is an ordinary actor bar.
- **Groups are contiguous slices by start order and actor number**, so they stay the same while panning; the decision depends only on the burst's own room and the zoom (never on the part in view). The sizes are not nested (50 to 20, 5 to 2): a finer group may straddle a coarser one's edge. Accepted for smooth steps (at most 2.5 times); a strictly nested chain (1, 5, 10, 50) would jump 5 times.
- **Each group is an ordinary group bar** (badge, stripes, hover label `#2466-#2470`, click lists its actors). `ActorBar.GroupLo/GroupHi` hold the lowest and highest actor number; the key is `g<slot>:<lo>:<hi>`. Selection (`groupSel` in `ScenarioDetailDialog`) narrows the side list to that range and shows a chip `actors #8977-#8981 x`; any bucket change or an actor pick clears it. The "burst - N actors" label still selects the whole burst (its title names the group size).
- **Failed, slow, running stay in their group (supersedes 0038).** A group turns red if any actor failed, else amber/graded by its worst ok actor (0041); the stripes show failed/slow/ok/running. **Running actors** of a live burst count into their group as "running" (grouping goes by actor number, not by state, so a group does not reshuffle when an actor finishes).
- **Previous/next failed (0036)** steps group by group inside a burst that is in groups (and selects the group, side list narrowed), actor by actor when the burst shows single bars.
- **Bounds.** A burst shows at most `UnfoldPerBurst` = max(24, max bars) bars; all bursts together at most `UnfoldBudget` = max(48, 2 x max bars) (raised from max bars, because the buffer around the view also unfolds and used up the budget while the groups in view were still coarse); when the budget runs short the farther bursts get coarser groups instead of staying folded. In practice the pixel rule (`MinActorPx`) bounds what is in view.
- **Deviation / known limit:** bursts come at jittered intervals, so the quiet time around each differs a little, and near a size threshold neighbouring bursts in one view can show different sizes (e.g. 20 and 50). It is stable (it does not flip while panning) but not perfectly uniform.
- **Debug:** `?burst=100` (1 to 1000) makes the seeded run's burst entries that big (and raises the concurrency limit, a few percent of actors fail).
