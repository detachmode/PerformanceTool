# Calls page keeps its state (W2, brain 0070)

Fake mode, seeded 30 minute run. Flow: endpoint far down the list, chronological sort, call list scrolled, a call picked, "Open B663", Back.

1. The picked call in the address (`call=663.0.0.13055`), call list scrolled, endpoint list scrolled.
   ![call picked](01-call-picked-1440.png)
2. "Open B663" opens the bee side panel; at 1920 the page makes room for it.
   ![panel 1920](02-panel-1920.png)
3. At 1440 the panel overlays the page (the page layout underneath does not change).
   ![panel 1440](03-panel-overlay-1440.png)
4. At 1100 the same, overlay.
   ![panel 1100](04-panel-overlay-1100.png)
5. Back: panel closed, the same call selected, the call list and the endpoint list where they were ("Load more" went to 400 calls to reach the call).
   ![back](05-back-same-call-and-scroll-1440.png)
6. Scenario page, bee B3457, Calls tab: click a call.
   ![bee calls](06-bee-calls-tab-1440.png)
7. The Calls page opens on that call's endpoint with the call selected and "‹ B3457 · Guest checkout" at the top.
   ![from bee](07-calls-from-bee-origin-1440.png)
8. Clicking it goes back to the bee's Calls tab (history back).
   ![back on bee](08-origin-link-back-on-bee-1440.png)
9. A call clicked in the side panel's Calls tab on the Calls page selects it in place.
   ![panel calls](09-panel-calls-tab-selects-in-place-1440.png)
10. Phone (390): the origin link above the endpoint, and the bee as a bottom sheet.
    ![phone](10-phone-390.png)
    ![phone sheet](11-phone-bee-sheet-390.png)
