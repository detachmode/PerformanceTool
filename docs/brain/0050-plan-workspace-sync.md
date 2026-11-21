# 0050. The server owns the Live plan (plan workspace) and the client syncs by polling

- Status: Proposed
- Date: 2026-11-21

## What
`PlanWorkspace` (host, singleton) holds the Live plan, the loaded preset name, the saved plan (for "unsaved changes" and
"entry changed") and a `Version` that grows with every real change. Presets persist through `PresetStore`.
REST (`Api/PlanEndpoints.cs`): `GET/PUT /api/plan`, `POST /api/plan/entries`, `DELETE /api/plan/entries/{i}`,
`GET /api/presets`, `PUT /api/presets/{name}`, `POST /api/presets/{name}/load`, `DELETE /api/presets/{name}`.
`POST /api/runs/start` without a plan starts the workspace plan.

`ApiRunSource` keeps a working copy the editor mutates in place (the editor has no change hook). A 400 ms sync loop
(Live mode only) compares the plan's JSON fingerprint with the last synced one: a changed plan that stopped changing is
pushed (`PUT /api/plan`, last writer wins); otherwise every third tick it asks `GET /api/plan?knownVersion=N` (204 when
unchanged) and adopts a newer server plan. Unpushed local edits are never overwritten by a pull.

## Why
The agent (0047) and the UI must edit one plan. Rejected: server push (SignalR/SSE) because polling is already how the
client follows runs and needs no new transport; a revision-based merge, because last writer wins is enough for one
user plus one agent. Fingerprint polling avoids touching every editor handler.

## Consequences
- An agent edit shows in an open editor within about 1.5 s. An edit made in the browser during that window wins.
- Deleting a preset bumps the version so open editors refresh their preset list.
- The restored run's plan seeds an empty workspace at startup (`SeedIfEmpty`).
