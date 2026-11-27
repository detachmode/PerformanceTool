# 0072. Clear labels, a readable Calls tab, one mode menu, readable faint controls

- Status: Accepted (user approved L2 to L5), implemented
- Date: 2026-11-27
- Builds on 0016 (calm UI), 0029 (every figure drills down), 0059 (scenario page), 0068 (menus, layout at every size), 0071 (the run bar stays).

## What
From a desktop UI test pass (plan items L2 to L5), one commit each:

- **L3a Run bar.** The main button always says what it does: "Start run", and "Running…" while a run goes (brain 0071: same place, same height, measured 65.8 px idle and running). The preflight is shown quietly next to it ("Preflight: check-readiness.cs", tooltip: runs once before the scenarios start, change it with Edit). It is not a second button and says no "tap" on desktop. Edit mode keeps its own pickers; their hints say "click" on desktop and "tap" on the phone.
- **L3b "+".** The unlabelled "+" next to Start run adds a preflight program (Live mode only). It is now the visible "+ Preflight" (dashed, small) with a tooltip and aria-label.
- **L3c Failed/slow arrows.** "‹ failed / failed ›" on the scenario page jumps to the previous / next failed (or slow) bee in the chart, in time order. It reads "Failed bee ‹ ›" (or "Slow bee"); each arrow has a tooltip ("Previous failed bee in the chart") and an aria-label. Not done: "3 of 109" (the position needs the chart's grouping; the arrows wrap around, the position adds a number to keep in sync).
- **L3d Step names.** A step's name in the step list (overview card, scenario page) opens **the logs of that step** (`/logs?scenario=n&step=m`), tooltip "Open the logs of the step X". Chosen because the counts already open the bees (failed at / in flight in), and the existing bee filters have no "reached this step"; the logs filter by step exist and work for every step, also one nobody failed at. The name is a button only where the page gives `StepTrack.OnLogs` (not in the assistant's bee-set dialog, which has no step list). Rejected: a new "reached step" bee filter (needs `StepFilter` and the address changed, see brain 0071 O1).
- **L4 Calls tab.** In the bee panel the name column is wider (3 : 1.2 instead of 2 : 3 against the bar) and a name too long for it is cut in the middle: the method and the start of the path may shrink with an ellipsis, the end of the path (`Format.CallNameParts`, at a "/" when there is one, at most 18 characters) always stays: "GET /products/…/availability". The tooltip has the full name and the URL.
- **L5 One mode menu.** The mode menu (Live / Fake mode, stored runs) was rendered twice (under the desktop button, and as the compact bar's menu). `PhoneState` now also knows the compact top bar (`IsCompact`, up to 1000 px, `window.beesharpCompact` in `index.html`, the same breakpoint as the CSS), and only the copy that is shown is rendered. `layout-sizes.mjs` no longer needs `.dd-menu:visible` (it uses `.mode-runs-menu`); `smoke.mjs` and old scripts using `:visible` keep working. The compact menu also shows for a phone turned sideways wider than 1000 px (before it was hidden by CSS there).
- **L2 Readable faint controls.** The "⋯" next to Edit was squeezed to 6 px (the pill's padding beat `.more-menu-btn`), it has its 20 px icon again. Disabled controls stay clearly disabled (lighter, default cursor, no hover) but legible: opacity .55 to .65 (were .35 to .45). `--faint` is `#8b90a2` (5.3:1 on the page, 4.8:1 on the panel, was 4.4 and 3.9), the edit cards' "Click to edit …" hint and the card's icon buttons use `--muted`, the dash for a zero count is about 3:1. Chart colours unchanged (L1 was dropped).

## Why
Labels should say what a control does, not what it is attached to; "tap" is wrong on a mouse; an unlabelled "+" and "‹ failed / failed ›" need a guess. A name that does nothing when clicked breaks 0029. The Calls tab is for telling calls apart, so the part that differs must stay. A DOM with two menus makes tests and screen readers deal with a hidden copy. Faint text and disabled controls under 3:1 cannot be read when it matters (a run is going).

## Consequences
- A test that needs the mode menu in a narrow window waits for `.mode-runs-menu`; there is one.
- `Format.CallNameParts` is the place for the cut; the CSS (`.cn-head`, `.cn-tail`) only shrinks the head.
- Screenshots: `docs/screenshots/2026-11-27-labels-l3-l5/`.
