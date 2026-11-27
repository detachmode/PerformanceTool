# 0069. A bee side panel, and "drill down, go back" lands where you were

- Status: Accepted (user approved W1), implemented
- Date: 2026-11-27
- Builds on 0016 (calm UI), 0029 (every figure drills down), 0059 (scenario page, Sheet, Logs page), 0066 (side panel).

## What
Found in a UI test: after a drill down the way back lost the previous state.
- **Bee side panel** (`Scenario/BeeSidePanel`): the scenario page's bee view (`BeePanel`: Steps / Logs / Calls, live logs, copy buttons) next to any page. A fixed right panel on desktop (under the top bar, above the player bar via `--player-h`, the page makes room by margin), the bottom `Sheet` on the phone. The page owns "open" and renders it only while a bee is open. The bee is found by its number (`Runs/BeeLookup`), so Fake mode, a running run and a stored run work. Header: scenario name, "Open in scenario ›" (link to the scenario page with the bee and tab), optional page actions, ✕. The Calls page can use it as is (W2).
- **Logs page:** a bee number in a log line opens the panel instead of filtering. The list, filters and scroll stay. The bee is its own parameter `open=4158` (from 1, not the `bee=` filter), pushed as a history entry (Back closes); ✕ / Escape go back when this visit pushed it, else replace. Another bee while open replaces the entry. "Only its lines" in the panel header applies the old bee filter; the `bee=` address and the search `B199` still work. A width change of the list (the panel opening) keeps the first visible row (`VirtualList` anchors on the row recorded before the reflow).
- **`beeTab=logs|calls`** (Steps = default, not written; `ScenarioLink.WithBeeTab/ParseBeeTab`, `Logs/LogPanelLink`) on the scenario page (history entry replaced) and in the panel, so Back from /calls lands on the Calls tab.
- **Overview edit mode in the address** `/?edit=1&entry=3` (`Overview/OverviewLink`, replace, not a history entry) plus `OverviewEditMemory` for the way back by the Overview tab. Old `?edit=<anything>` links still open the editor.
- **Scroll memory:** `Shared/ScrollMemory` (+ `wwwroot/scroll-memory.js`, sessionStorage, key per page and list) for the Overview (keys `overview`, `overview-edit`), Environment and the Logs filter column; `VirtualList MemoryKey` remembers the first visible row for the Logs lines (key with run and filter) and the scenario page's bee list. A list left at its end that follows it opens at the end again. Calls page: the same two pieces (W2, brain 0070).

## Known limits
- Restoring a variable-height list (wrapped log lines) lands within a few rows of the remembered one on desktop, not on the pixel (row heights are estimated again). On a phone with very tall wrapped lines (Fake mode, 390 px) the Logs list can come back far from the remembered row (seen: row 245 became 81); fixed-height lists (the scenario's bee list) are exact. Open: anchor the restored row after measuring.
- The Editor's file menu is `DropMenu` now (V-work); no scroll memory there.
