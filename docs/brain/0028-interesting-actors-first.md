# 0028. Failed and slow actors first: colour, order and retention

- Status: Accepted (user decisions)
- Date: 2026-11-20

## What
Failed or slow actors are usually more interesting to the user than successful ones. So, across the whole UI:
- **Actor numbers carry the outcome colour** (`#192` in red / yellow / green / blue for running) wherever they are
  shown: logs, popup lists, labels, chips.
- **General ordering rule for actors:** **failed → slow → successful**, then by time. This applies wherever actors are
  listed or grouped (e.g. the actors of a time bucket, actor lists, log filtering by actor). Scenario cards keep their
  plan order so they don't jump around.
- **Retention priority:** when the limit of kept detail is reached (calls/logs, currently 1500 actors per scenario,
  configurable), **successful actors are dropped first**, then slow, then failed. Failed and slow actors are kept longer.
- **Error dictionary:** store each distinct error (message, exception text) once and reference it from actors/logs,
  to save memory and, later, disk space.

## Why
Users investigate problems. The UI and the storage should spend their attention and space on the actors that went
wrong, not on the many that went fine.

## Implementation notes (data side)
- `RunStoreOptions.MaxDetailedActorsPerScenario` (default 1500, settable live) is the single limit. `RunStore` keeps one queue per outcome per scenario and evicts Ok, then Slow, then Failed, oldest first. Only finished actors are in the store, so running actors are never evicted. `RunStore.HasDetail(sequence)` is the cheap "is detail kept" check.
- `ErrorDictionary` (one per run, `RunStore.Errors`) interns error texts: actor errors, call errors, exception texts and repeated log messages (e.g. "Retrying partner call"). `ActorRecord`, `FakeCall` and `FakeLogEntry` hold ids; `Error`, `Message`, `Exception` resolve them. Unique messages (they contain numbers) stay plain strings on purpose, interning them would only grow the dictionary.
- Fixed a leak found while measuring: evicted actors stayed referenced by the not-yet-indexed list until the Logs popup was first opened. The list is compacted now.
- Measured with `?simulate=3h` (36k actors, 5 scenarios, .NET 10 console harness, GC total memory): after generation 82.2 MB before, 20.7 MB after; with all logs built 107.9 MB before, 88.1 MB after, although 32k instead of 7k log lines of failed actors are kept (all kept lines: 139.5k). In the 3 h simulation the Logs popup now finds 32,158 lines of failed actors (before 7,219) and 116 "timeout" lines (before 42).

## Implemented
- `Components/ActorOrder.cs` is the one place: `ActorState` (failed, slow, running, ok), the comparer (state, then start time, then sequence) and the CSS class of the outcome colour. Running actors sort between slow and ok (they have no outcome yet; my choice, not recorded as a user decision).
- `Components/ActorChip.razor` renders `#192` in the outcome colour (red, yellow, green, blue) and is used in the Logs popup (row chips, the actor filter chip, the detail head), the popup side list, the actor panel heads and the hover card (`dz-id`, neighbour labels). The outcome is also in the accessible name.
- The popup's side list and the group's actors use the comparer. The Logs popup lists lines chronologically, so ordering does not apply there; it has no actor suggestions yet.

- **Proposed (agent choice, not user-decided):** running actors are ordered between slow and successful.
