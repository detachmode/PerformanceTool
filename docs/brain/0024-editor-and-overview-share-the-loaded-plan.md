# 0024. Editor and overview show the same view of the loaded scenarios

- Status: Accepted (user decision)
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
