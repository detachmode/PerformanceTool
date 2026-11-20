# 0019. Visible selection and immediate feedback

- Status: Accepted (user feedback); concrete design via drafts
- Date: 2026-11-20

## What
User feedback on the drafts: "the UX is quite bad".
- **It's hard to see what is currently selected** (which scenario, which actor, which filter). The selected
  thing must be unmistakable wherever selection exists: the overview, the editor list and the popups.
- **The editor feels really bad: there's no feedback when something changed.** Every change must give immediate,
  visible feedback: what changed, whether there are unsaved changes, and when something was saved or is invalid.

## Why
Without visible state, the user can't trust the tool or tell what will happen next. It also undermines the easy
navigation goal (0017).

## Consequences
- Draft reviews check that selection and change state are visible at a glance, without adding clutter (0016) and
  using colour only with meaning (0018).

## Update (user feedback, 2026-11-20)
- The editor must **detect a change while typing**: the Save button (and "Unsaved changes") must enable as soon as the
  value changes, not only when the input loses focus.
- Some **scrollbars are missing** in the editor (content can't be reached); fix overflow so every area that can grow
  scrolls.

### Implemented (editor fixes)
- **Change detection while typing:** every generated input (text, URL, multiline, secret, number, slider, duration, list items,
  key/value rows) and the label, rate and plan settings update on `input`. The dirty state is derived from the live plan on every
  render, so "Unsaved changes", Save and the card's "Changed" marker react at once, and reverting the text removes them again.
  Components raise `Edited` (typing) and `Committed` (leaving a field, buttons, toggles) to the page.
- **Calm validation:** errors are shown from a snapshot taken on blur or after a 300 ms pause in typing, never per keystroke.
  Only the selected entry is validated (cheap per keystroke).
- **Scrolling:** the settings panel is a flex column (sticky head with Unsaved + Save, scrolling body, sticky foot with
  "Remove scenario"). Before, a tall panel was cut off at the bottom of the window (sticky, no max height) and the phone sheet's
  header scrolled away. The panel is capped to the visible area (minus the player bar); on phones the sheet is full screen with
  the same layout. Multiline fields are capped (40vh, resizable). The page itself scrolls for the cards grid and presets.
