# 0034. Bursts: a spawning pattern per scenario

- **Status:** Accepted (idea from the user; details open)
- **Date:** 2026-11-21
- **Related:** 0004 (rate = actors per minute), plan item "Ramp-up profiles"

## Context
Each scenario entry starts new actors at a constant rate (actors per minute). Real traffic comes in waves
and spikes.

## Decision (user)
Add **burst definitions per scenario entry** that define the spawning pattern (how new actors start over time).

## Open (to decide with the user, one at a time)
- Which patterns: e.g. a spike of N actors at once, repeated waves, a ramp from one rate to another.
- How a pattern is entered in the editor and shown on the cards and charts.
