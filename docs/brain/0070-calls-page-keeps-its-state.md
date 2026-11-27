# 0070. The Calls page keeps its state when you drill down and come back

- Status: Accepted (user approved W2), implemented
- Date: 2026-11-27
- Builds on 0016 (calm UI), 0029 (every figure drills down), 0060 (Calls page), 0068 (layout at every size), 0069 (bee side panel, scroll memory).

## What
Found in a UI test: endpoint picked, call list scrolled, call picked, "Open B…", Back: the same endpoint but another selected call (the slowest) and both lists at the top.

- **The selected call is in the address**: `calls?endpoint=…&sort=time&call=4668.2.0.153120`, written with replace (`Calls/CallsAddress`). The sort order was already part of the filter address (`sort=`).
  - **Call key** `<bee>.<step>.<rep>.<offset in tenths of a ms>` (`CallKey`): the bee as the UI numbers it (from 1), the step and repetition, the offset in the bee. Not the call id: Live mode numbers calls in the database (DuckDB `calls.id`), only Fake mode and the in-memory store derive it from bee and index, so an id is not the same across run sources; the key is the same in Fake and Live and is what the bee view (`FakeCall`) also knows, so a click in a bee's Calls tab can name the call without a query.
  - **Load more depth:** a call from the address (link, reload, Back) that is not in the first 200 calls makes the page load one more page at a time until it is there (`SyncSelectedCall` returns "need more", `callLimit += 200`), up to what one request may ask for (1000, `CallAnalytics` clamps there). A call the user picked or a filter the user changed never loads extra pages (`chaseCall` is only set by the address). If the call is gone (another run, trimmed Fake detail), the first call is selected and the address says so.
  - A changed filter keeps the selected call when it is in the new list (a new sort order does), else the first one; a new endpoint or Overview forgets the call. `CallsLinks.LastCall` brings the call back together with `Last` when the menu's "Calls" is used.
- **Scroll memory** with the two pieces of 0069, no new mechanism: `ScrollMemory` for the endpoint list (`calls-endpoints`, bound once the list has rows), the right column (`calls-right:<endpoint>`) and the window (narrow widths scroll the page); `VirtualList MemoryKey` for the call list (`calls:<run>:<filter query>`) plus `ScrollToIndex` so a call from a link is brought into view (the remembered place wins over it).
- **Bee side panel instead of navigating away:** "Open B…", the bee chip of the selected call and the bee number in a call row open `Scenario/BeeSidePanel` (`open=<n>`, `beeTab=`, pushed as a history entry; Back, ✕ and Escape close it; another bee replaces; on the phone the bottom sheet), like the Logs page. Its "Open in scenario ›" leads to the scenario page. Clicking a bee number in a row also selects that row's call. A call clicked in the panel's own Calls tab selects it in the page in place (`BeePanel.OnCallOpen`); on the phone the sheet closes first (history back), then the call is selected.
  - **Layout:** the Calls page has its own left column and the call detail column, so it makes room for the panel (margin) only from 1560 px; below that the panel overlays the page (1440, 1100, 900) and the page keeps its layout underneath. Between 861 and 1559 px the panel covers the right part of the page; 360 to 480 px wide (`--bee-side`).
- **Coming from a bee:** a click on a call in a bee's Calls tab or steps (scenario page or side panel anywhere) opens the Calls page on that call's endpoint (the scenario filter stays the bee's) with the call selected and a small "‹ B4668 · Checkout" link at the top of the right column. The address carries it as `origin=scenario:1:4668` (`CallsOrigin`, via `CallsLinks.ShowCall`). The brief suggested `from=`, but `from` is already the Calls page's time window (`from=`/`to=`), so the parameter is `origin`.
  - The link goes **history back** when this visit of the page was opened out of that bee (`CallsLinks` remembers the arrival once, in memory) and no panel entry was pushed since; otherwise (reload, a shared link, browser Back to the page) it navigates to the scenario page with that bee and `beeTab=calls`. Going back keeps the bee list's place on the scenario page (0069).

## Alternatives rejected
- Call id in the address: not the same in Live and Fake mode, not stable between runs of the in-memory and DuckDB stores.
- A bee-and-index key: the Calls page only gets `CallSampleDto`, which has no index; the step, repetition and offset identify a call in its bee just as well and are in both models.
- Squeezing the Calls page for the panel below 1560 px: the left column plus list plus a 21 rem detail column already need about 1000 px; overlay is calmer than three cramped columns.

## Known limits
- A call beyond the 1000th of an endpoint's matching calls (`CallAnalytics` clamps one request at 1000 and "Load more" only deepens the same window) cannot be reached by address or by Load more; the first call is selected instead. Paging by offset would lift it.
- Within one visit, returning to a filter seen before restores the list position of that filter (the memory key includes the filter), while the selection after a filter change is the first call.
- The endpoint list restore gives up after about a second when the list is not tall enough yet (Live mode with a slow first load): it then stays at the top.
