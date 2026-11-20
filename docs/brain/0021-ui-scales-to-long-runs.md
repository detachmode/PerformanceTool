# 0021. The UI must scale to long runs with many calls

- Status: Accepted (user requirement); techniques are implementation choices
- Date: 2026-11-20

## What
The UI must not break, slow down or become unreadable when a run has been going **for hours** and has produced a very
large number of actors, calls and log lines.

## Why
Real load runs can be long. A UI that only works for a 30-second demo is useless in practice.

## Consequences (guidance for implementation)
- Never render one DOM element per actor, call or log line for the whole run:
  - virtualise long lists (Blazor `Virtualize`);
  - aggregate charts into time buckets, so the number of bars is bounded and independent of run length.
- Statistics are computed incrementally, not by re-scanning every result on each render.
- The client holds bounded data. Older detail is fetched on demand (paged/filtered queries against the API later;
  the fake source should behave the same way).
- The fake source must be able to simulate a long, large run quickly, so scaling can be tested.
