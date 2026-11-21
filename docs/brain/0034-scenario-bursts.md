# 0034. Bursts: a spawning pattern per scenario

- **Status:** Accepted, implemented (fake data)
- **Date:** 2026-11-21
- **Related:** 0004 (rate = actors per minute), plan item "Ramp-up profiles"

## Context
Each scenario entry starts new actors at a constant rate (actors per minute). Real traffic comes in waves
and spikes.

## Decision (user)
Add **burst definitions per scenario entry** that define the spawning pattern (how new actors start over time).

- Two spawning patterns per scenario entry:
  - **Linear:** just **actors per minute** (today's behaviour, the default).
  - **Bursts:** **actors per burst** and **bursts per minute**: each burst starts that many actors at once.

## Proposed (Claude, not decided)
- Editor: a small "Linear / Bursts" switch on the scenario entry; Bursts shows the two numbers instead of
  actors per minute.
- Cards and player bar show the resulting actors per minute (actors per burst × bursts per minute), with the
  burst shape in small text (e.g. "10 × 4/min").

## Implementation notes
- `ScenarioRunConfig` has `Pattern` (`SpawningPattern.Linear` | `Bursts`, stored as a string), `ActorsPerBurst` (default 10) and
  `BurstsPerMinute` (default 4) next to `RatePerMinute`. A missing pattern in an old preset means linear, so old presets load unchanged.
  `EffectiveRatePerMinute()` is the one place for actors per minute (linear: the rate; bursts: actors per burst x bursts per minute).
- Validation: actors per burst 1 to 1,000, bursts per minute > 0, and the effective rate at most `MaxRatePerMinute`.
- `LoadRunner`: a burst tick starts all its actors together; each still takes a max in-flight slot, the rest are dropped as before.
- `FakeRunSource` fires a burst's actors at the same moment, so actors arrive in clumps; the fake preset's "Browse categories" is 15 x 6/min.
- Editor: "Spawning" Linear/Bursts switch; Bursts shows "Actors per burst" and "Bursts / minute" (with "= N actors/min") instead of "Actors / minute".
  Cards, popup and player bar show the effective actors/min; cards and popup add the shape in small text ("15 x 6/min").
