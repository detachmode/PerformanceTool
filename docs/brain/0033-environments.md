# 0033. Environments: run the same scenarios against different servers

- **Status:** Accepted (content and usage decided; where they live and UI open)
- **Date:** 2026-11-21
- **Related:** 0003 (scenarios in code), 0005 (presets), 0007 (generated editor)

## Context
Scenarios name the servers they call (for example a base URL in their parameters). To run the same scenarios
against another server (local, test, staging), you would have to change every scenario entry.

## Decision (user)
Support **environment definitions**, so scenarios can run against different servers without changing them.

- An environment holds **arbitrary key-value pairs** (e.g. a base URL, a tenant, a user name), and scenarios
  can use them. **Credentials are allowed too**: what goes in is up to the user.
- Scenarios use the values **both ways**, like Bruno (the API client) does:
  - **Placeholders** in parameter values, written `{{BaseUrl}}` (Bruno syntax, no leading dollar sign). The
    active environment fills them in when the run starts.
  - **In scenario code**, e.g. `env["BaseUrl"]`.
- *Proposed (Claude, not decided):* like Bruno, let a value be marked **secret** so it is masked in the UI and kept
  out of shared files (stored locally only).

## Open (to decide with the user, one at a time)
- How a scenario refers to it, and where environments are defined and chosen (plan, preset, editor, player bar).
