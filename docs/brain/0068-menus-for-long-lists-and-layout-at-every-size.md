# 0068. Menus for long lists, and a layout that holds at every window size

- Status: Accepted (user request V1 to V4), implemented
- Date: 2026-11-27

## What
**Menu pattern for lists that can grow** (`Shared/DropMenu.razor`, `wwwroot/drop-menu.js`, `.dd-menu.parts` in `app.css`):
- A menu is a backdrop plus a popup with three parts: a fixed **header** (`Head`: labels, mode switch), the **list** (`.dd-scroll`, the only part that scrolls, max height from the window) and a fixed **footer** (`Foot`: Save, Save as new…, Configure…, New…, Import…). Actions never sit below the entries.
- A **filter field** shows from `DropMenu.FilterFrom` (10) entries; it is focused when the menu opens (not on touch screens).
- The **current entry** (`.on`, `aria-checked`, `aria-current`) is scrolled into view when the menu opens.
- Long names truncate with an ellipsis and carry a `title` tooltip; the popup is at most 26 rem wide.
- Used by the Preset menu, the mode menu (stored runs, now up to 200 instead of 30, filter by date or "failed"), the Environment dropdown and the Editor's file menu. Pickers that are not popups follow the same idea: the "Add scenario" and preflight/shutdown pickers scroll inside a capped list with a filter, "Create new…" stays pinned; the Logs page's scenario group scrolls (with filter) so the steps below it stay in reach; the Environments dialog has a fixed height, its list scrolls with a filter and "+ Add environment" below it, and Save stays in a sticky footer.
- **Button order** (dialogs, confirm bars): secondary or cancel first, the **primary action last (right)**, a destructive action apart from the primary one (Environments dialog: "Delete environment" left in the footer, Save right).

**Layout at every size** (V3): `tests/e2e/layout-sizes.mjs` opens the app in Fake mode at 12 widths (1920 down to 390) and two heights and fails on a horizontal page scrollbar, an overflowing or overlapping top or player bar, and a fixed side panel that does not reach from the top bar to the player bar.
- `--player-h` (77 px) is the one height of the player bar; panels (`.ed-side`) and the Calls, Logs and Editor pages use it. The scenario settings panel now fills the window height (it had `bottom: 4.6rem` for a player bar that is not shown in edit mode, leaving a black gap).
- In edit mode (desktop) the settings column is **always there**, with a hint while no scenario is selected, and the page gives up the width of the column beyond the free margin of the centred page. So selecting a scenario never reflows the card grid (refines 0066).
- The top bar switches to the compact variant (hamburger drawer) below 1000 px instead of 860 px; the build stamp goes first (1320 px), then the brand name and the environment label (1180 px); the breadcrumb and the environment name shrink.

**Calls (V4)**: the duration distribution is a compact 16.5 rem card beside the over-time chart (stacked, still compact, when the pane is narrower than 640 px); the selected call is a detail column beside the list (grid of figures, URL and trace id with copy buttons, actions at the bottom, the scenario filters the list). The right pane is a CSS size container, so the layout follows the pane and not the window.

**Colour (V1)**: failed and slow bars are solid mixes of the colour with the panel (`--failed-bar`, `--slow-bar`) instead of lowered opacity, which read brown and orange on the dark panel (refines 0018).

## Why
With about 100 presets, environments, stored runs and 30 scenarios the menus pushed Save and Configure out of reach, lost the current entry and made the lists unreadable; in narrow windows the top bar and the settings panel broke. A menu that scrolls inside, with its actions fixed, and a script that measures the layout keep this from coming back.

## Consequences
- A new menu that can hold many entries uses `DropMenu` (and `FilterFrom`); a new fixed panel uses `--top-h` and `--player-h`; run `layout-sizes.mjs` after layout changes.
- Rejected: virtualising the menu lists (100 to 200 plain buttons are cheap); a hard cap on the stored runs shown (the filter is enough).
