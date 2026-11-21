# 0034. Bursts: a spawning pattern per scenario

- **Status:** Accepted (not implemented)
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
