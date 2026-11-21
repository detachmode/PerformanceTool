# 0033. Environments: run the same scenarios against different servers

- **Status:** Accepted, implemented (fake data)
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

## Implementation notes (Claude, design choices)
- **Core:** `EnvironmentDefinition` (name + ordered `Variables`), `EnvironmentValues` (what code reads: `context.Environment["BaseUrl"]`; a missing key throws a
  message naming key and environment; `Get(key, fallback)` for optional ones) and `Placeholders` (find, split for highlighting, resolve, resolve a whole parameter JSON).
- **Syntax:** `{{Key}}` (spaces inside allowed), keys are letters, digits, `_ . -`. `\{{Key}}` is the escape. Inserted values are not scanned again.
  `${env:NAME}` is untouched. Only string values are resolved (also inside lists, groups, key-value maps), not property names.
- **Unknown keys:** the resolver leaves the text and reports them. The runner refuses to start and names the keys (a run with literal `{{BaseUrl}}` URLs would only
  fail every actor). The editor shows a quiet note and a dashed placeholder.
- **Validation:** `[HttpUrl]` accepts a value with a placeholder (it can only be checked once the environment fills it in). A secret field holding only a placeholder
  is kept when saving presets (it holds no secret itself).
- **Server:** `EnvironmentStore` (`Environments:Directory`, default `environments`), one file per environment, atomic replace, validated on save; `GET/PUT/DELETE /api/environments`;
  `LoadRunner.RunAsync(..., environment)` resolves once at run start and passes the values to `ScenarioContext`. Which environment is active is chosen by the caller of the run.
- **Client:** `IEnvironmentSource` (fake in-browser, `ApiEnvironmentSource` stub). The active environment is not part of a preset and is not remembered across reloads yet.
  Keys that look like credentials (token, secret, password, key, auth...) are masked in editor hints and the popup until "Show"; this is a display convenience, not the
  Proposed secret flag. The popup keeps one draft per environment, so switching never loses edits; closing with unsaved drafts asks first.
- **Editor hint (calm UI):** the placeholders are highlighted under the field; the resolved text shows on hover or focus (always on touch). A placeholder opens the
  popup on its environment, an unknown key adds a row for it (brain 0029).
