# 0033. Environments: run the same scenarios against different servers

- **Status:** Accepted (content decided; usage and UI open)
- **Date:** 2026-11-21
- **Related:** 0003 (scenarios in code), 0005 (presets), 0007 (generated editor)

## Context
Scenarios name the servers they call (for example a base URL in their parameters). To run the same scenarios
against another server (local, test, staging), you would have to change every scenario entry.

## Decision (user)
Support **environment definitions**, so scenarios can run against different servers without changing them.

- An environment holds **arbitrary key-value pairs** (e.g. a base URL, a tenant, a user name), and scenarios
  can use them. Secrets stay out of files as before (`${env:NAME}` placeholders).

## Open (to decide with the user, one at a time)
- How a scenario refers to it, and where environments are defined and chosen (plan, preset, editor, player bar).
