# 0020. Simple first: get the core flow right, then add features one by one

- Status: Accepted (user decision)
- Date: 2026-11-20

## What
Too much has been added to the UI already. **Keep things simple for now** and add features **one by one**.
First, get the **initial UX** of the core flow right:
1. **Setting up scenarios**
2. **Running them**
3. **Saving and loading them as presets**
4. **Inspecting the run**

Anything not needed for this flow is removed from the UI for now, or not built yet. It comes back later as its own
step, only when the user asks for it.

## Why
The drafts grew features faster than the basic experience matured. That overloads the user (0016) and makes the
core flow feel bad (0019).

## Consequences
- Earlier draft extras (filter chips, sort/group menus, many statistics, call heatmaps, demo extras, …) are not
  part of the core flow. Whether each one returns is decided one at a time.
- Background agents get small, single-feature briefs. The next agent is only started after the user agrees on the step.
