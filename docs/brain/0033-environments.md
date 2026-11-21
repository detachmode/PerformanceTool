# 0033. Environments: run the same scenarios against different servers

- **Status:** Accepted (not implemented)
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
- **One file per environment**, like Bruno: each environment is its own file in an environments folder (next to
  the presets), so it can be shared or kept local file by file.
- *Proposed (Claude, not decided):* like Bruno, let a value be marked **secret** so it is masked in the UI and kept
  out of shared files (stored locally only).

- **UI like Bruno:** an environment dropdown in the top bar picks the active environment; an "Configure"
  entry in it opens a popup to edit environments (list of environments, key-value table per environment).
