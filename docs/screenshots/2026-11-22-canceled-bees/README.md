# Canceled bees (brain 0053)

Bees in flight when the run is stopped are kept as canceled: they look and count like failed (red, in the errors), with the error "Canceled: run stopped", their logs and their calls. The call cut off by the stop is failed (status "canceled") with the time it ran until the stop.

## Live mode (server), Multi-Step Order Flow, stopped after about 7 s

Overview after the stop: 6 of the 11 bees were in flight and are counted as errors.

![Overview after stop](live-02-overview-after-stop.png)

Popup chart: the canceled bees are the red bars, shortest bar = the youngest bee.

![Popup](live-03-popup.png)

A canceled bee with the logs it already had:

![Bee detail](live-04-bee-detail.png)

Its calls: finished calls unchanged, the cut-off call failed.

![Waterfall](live-05-bee-waterfall.png)

## Fake mode

![Overview after stop](fake-02-overview-after-stop.png)

![Popup](fake-03-popup.png)

![Waterfall](fake-05-bee-waterfall.png)
