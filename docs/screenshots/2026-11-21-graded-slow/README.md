# Slowness as a colour gradient (brain 0041)

Published (trimmed) Release build, Playwright. Desktop = 1280 px at device scale 2, phone = Pixel 7. The finished run is a 5 minute one
(`?simulate=5m`) so that the calls of old actors are still kept. Scenario entry "Browse categories" (bursts of 15 actors).
Green = typical time or faster, then three steps of yellow-green, full amber = slow (over 2x typical).

| | Desktop | Phone |
|---|---|---|
| Overview (card sparks) | ![](desktop-01-overview.png) | ![](phone-01-overview.png) |
| Popup, whole run (group bars take their worst actor) | ![](desktop-02-popup-unzoomed.png) | ![](phone-02-popup-unzoomed.png) |
| Zoomed | ![](desktop-03-zoomed.png) | ![](phone-03-zoomed.png) |
| Unfolded burst: the gradient bar by bar | ![](desktop-04-burst-unfolded.png) | ![](phone-04-burst-unfolded.png) |
| Selected actor with its "x typical" figure | ![](desktop-05-actor-selected.png) | ![](phone-05-actor-selected.png) |
| The figure clicked: typical time of each call | ![](desktop-06-actor-typicals.png) | ![](phone-06-actor-typicals.png) |
| Logs popup (actor numbers carry the colour) | ![](desktop-07-logs.png) | ![](phone-07-logs.png) |
