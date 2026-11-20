# 0024. Editor and overview show the same view of the loaded scenarios

- Status: Accepted (user decision); implemented (first draft)
- Date: 2026-11-20

## What
The editor and the overview should present the **currently configured scenarios** in a **similar, recognisable
way**. The user must be able to tell that the editor edits **the currently loaded scenarios** (the loaded plan), not
a preset. Presets are only loaded into it and saved from it.

## Why
Today the editor (a list plus detail panel) and the overview (cards) look unrelated, and the editor's preset controls
make it feel like you're editing a preset file. That's confusing.

**Decided:** the editor uses **the same scenario cards as the overview** ("they look nicer"). In the editor, a card
opens that scenario's settings.

## Open (to decide with the user)
- How presets are shown as "load into / save from" the current scenarios.

## Implemented (draft)
- The editor shows the same `ScenarioCard` as the overview, in an idle state (`Idle`): label, actors/min, scenario type, "Not run yet". No run data, because the editor edits the plan, not a run.
- Clicking a card selects it (`.card.selected`: accent border and background, `aria-pressed`) and opens its settings in a side panel next to the grid: label, actors/min, generated `ParameterEditor`, "More options", "Remove scenario". Up to 1000px wide the panel is a full-screen sheet with a close button.
- "+ Add scenario" is a dashed card-shaped tile at the end of the grid.
- Presets: header "Loaded from preset ..." with a one-line hint; the confirm and Save as texts say a preset is loaded into / saved from the scenarios. "Unsaved changes" and "Saved ✓" stay.
- Changed scenarios (new or different from the loaded preset) get a dot and "Changed" on their card (`IRunSource.IsEntryChanged`). Reverting a value removes the marker.
- Screenshots: `docs/screenshots/2026-11-20-editor-cards/`.
- Still open: whether the editor cards should show the last run's bars once a run exists.
