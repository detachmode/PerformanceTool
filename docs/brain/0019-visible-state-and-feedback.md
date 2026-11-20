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
