# 0071. The overview stays still while a run goes

- Status: Accepted (user approved X), implemented
- Date: 2026-11-27
- Builds on 0016 (calm UI: nothing jumps), 0029 (every figure drills down), 0056 (scenario steps, folding), 0068 (layout at every size).

## What
Measured in a UI test (Fake mode, `/?stepcount=8,14`, Start run, 1440x900, a sample every 300 ms for 30 s): the cards of the second row changed height between 370 and 482 px about once a second (6 and 7 distinct heights per card, the rows below moved by up to 232 px), and starting a run moved the first row of cards up 82 px. Both break "nothing jumps".

- **Folding of steps is by position, never by activity** (`Chart/StepTrack`). When there are more steps than fit (6 on the card, 10 on the scenario page) the first and the last step show and the steps between are cut into `max - 2` even groups. Which steps form a group never depends on bees in flight or failures, so the number of rows is the same all run long. (Before, a run of quiet steps folded and unfolded as bees moved through it.)
- **A group row keeps its figures:** the sum of its steps' "Now" and "Failed" counts, and "has bees / has failures" tinting, so a failure inside a group is still visible on the card. Rejected: showing a step as soon as it has a failure (the rows would still change, once per failing step, and a failure would push the cards down in the middle of a run).
- **Drill down (0029):** a group's name and both counts open the group (its steps show, each with its own clickable counts that open the bees). That is the user's own action, so the height may change then. The group holding the step the scenario page filters by is open. Not done: opening the bees of all steps of a group in one click (the step filter and the address `failedStep=`/`nowStep=` name one step; a range would touch the address, the bee list and the filter chip).
- **The run bar stays** (`Pages/Overview`): the bar with "Start run" and the duration is not replaced while a run goes. It stays in place at the same height, disabled, the button says "Running…". The player bar at the bottom still holds the run's controls (0027), nothing is duplicated.
- **Preflight and shutdown strips go below the cards** (`PreflightStrip`, `ShutdownStrip`): they appear when a program finishes or runs, and above the cards they pushed them down by their own height. Below, nothing that is already on screen moves. (The running/failed preflight console still replaces the cards, as before: nothing is shown yet that could move.)
- **Cards reserve their space:** the card's foot line has a minimum height (the "slowest 5%" link made it 3 px taller as soon as the first bees finished), the step list's header row has a fixed height (the "Now" column header wrapped to two lines on the scenario page and made the list 8 px taller when a run started or ended).

## Why
A load test is watched for minutes. Cards that breathe once a second make the numbers hard to read and clicks land on the wrong thing. Position-based folding is the simplest rule that is stable by construction (no hysteresis to tune), and it keeps every figure reachable (0029).

## Known limits
- In Live mode the step list of a scenario is only known when the run's status arrives; a card that has no steps yet grows when they arrive (once, at the start). Fixing it needs the scenario catalog to expose the declared steps before a run.
- A shutdown program that fails is now reported under the cards, one scroll away on a page with many cards.

## Update (user, 2026-11-27): the strips go back above the cards
With many scenarios a failed preflight or shutdown below the cards was out of sight. The user chose visibility: `PreflightStrip` and `ShutdownStrip`
sit above the cards again. They appear at most once per phase (preflight at the start, shutdown at the end), so the cards move by a strip's height then.
