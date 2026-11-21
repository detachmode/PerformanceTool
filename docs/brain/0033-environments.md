# 0033. Environments: run the same scenarios against different servers

- **Status:** Accepted (idea from the user; details open)
- **Date:** 2026-11-21
- **Related:** 0003 (scenarios in code), 0005 (presets), 0007 (generated editor)

## Context
Scenarios name the servers they call (for example a base URL in their parameters). To run the same scenarios
against another server (local, test, staging), you would have to change every scenario entry.

## Decision (user)
Support **environment definitions**, so scenarios can run against different servers without changing them.

## Open (to decide with the user, one at a time)
- What an environment holds (only server addresses, or also other values such as secrets via `${env:NAME}`).
- How a scenario refers to it, and where environments are defined and chosen (plan, preset, editor, player bar).
