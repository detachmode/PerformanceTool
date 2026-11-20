# 0022. Core-flow UI: implementation choices (slow rule, bounded data, own virtual list)

- Status: Accepted (implements 0016, 0018, 0019, 0020, 0021); the slow rule is a **Proposed placeholder**
- Date: 2026-11-20

## What
- **Slow (placeholder, not decided by the user):** a call is slow when it takes more than 2x the typical duration of that same call
  (median of a sliding window of the latest 51 durations, needs at least 5 samples). An actor is slow if any call is slow. The rule lives
  only in `Services/SlowCallRule.cs`. Cards and bars are red when errors occurred, amber when only slow calls occurred.
- **Bounded data:** `RunStore` keeps a tiny record per finished actor for the whole run, counters incrementally, and calls and log
  lines only for the latest 1500 actors per scenario (the UI says so). The log index is a time-sorted list that is merged incrementally.
- **Bounded charts:** up to 48 bars is one bar per actor, more is aggregated into time buckets of a nice width (red/amber if any actor in
  it failed/was slow); a bucket opens a virtualised list of its actors. Card mini charts show the latest 32 actors.
- **Own `VirtualList` (small JS scroll reporter) instead of Blazor `Virtualize`:** with .NET 10, `Virtualize`
  mis-estimated row heights with 100k+ rows and the list went blank after scrolling. It has a fixed-height mode (actor lists) and a
  **variable-height mode** (log rows, which show the full message word-wrapped, `overflow-wrap: anywhere`; the user asked for this, superseding the
  earlier fixed two-line rows). Variable mode: unmeasured rows use an estimate, the rendered rows are measured after each render, heights are cached
  per item, offsets are kept as prefix sums (binary search for the window, one shifting pass per measurement). A JS MutationObserver keeps the first
  visible row in place when rows appear above it (before paint); a width change clears the cache and keeps the first visible row. Scope, call and
  exception still show in the detail panel under the list.
- `?simulate=3h` (or `90m`, `600s`) pre-generates a long run to test scaling.

## Why
The user wants the simple core flow first (0020) that still works for runs of hours (0021).
