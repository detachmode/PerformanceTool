# 0039. The fake data starts with a 30 minute finished run

- **Status:** Accepted (user), implemented
- **Date:** 2026-11-21
- **Related:** 0021 (scale), 0024 (editor and overview share the loaded plan), 0034 (bursts), 0038 (unfold bursts)

## Context
The first load of the fake data showed a finished run of 30 seconds. That is too little to test zooming, grouping and bursts.

## Decision (user)
The pre-seeded finished run in `FakeRunSource` is **30 minutes** long (`FakeRunSource.SeedRunSeconds`). `?simulate=` still overrides it. The "Black Friday mix" plan has a bursts entry ("Browse categories", 15 x 6/min), so the run holds about 180 bursts to zoom into.

## Implementation notes
- The seed loop now always steps 0.25 s (it was 0.05 s for the short run). First load of the published (trimmed) build to the first scenario cards: about 1.4 s measured in headless Chromium (desktop), with 2,715 actors in the Browse categories entry and about 7,000 in all.
- **Editable plan vs seeded run:** the editable plan (Black Friday mix) keeps its own duration of 30 s, the seeded run is 30 minutes. This does not contradict 0024 (editor and overview show the loaded plan): the plan describes what a next run will do, the seeded run is a finished past run that exists only to have data to look at; "Run again" starts a run with the plan's own duration. If this reads as odd the next step is to make the plan 30 minutes too.
