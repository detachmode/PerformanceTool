# 0001. Blazor Server (interactive server rendering) on .NET 11

- Status: Accepted
- Date: 2026-11-20

## What
The app is a single ASP.NET Core project (`src/PerformanceTool`) using Blazor Web App with **interactive
server** render mode, targeting `net11.0`. Interactive pages must declare `@rendermode InteractiveServer`.

## Why
- The load is generated **by the server** with `HttpClient`. A browser can't do that well: CORS, connection
  limits per host, and the tab's own performance would distort the numbers.
- Server rendering streams UI updates over the existing SignalR circuit, so the live chart needs no extra
  API, polling, or JavaScript.
- .NET 11 was requested explicitly.

## Consequences
- Every open tab holds a circuit; a run belongs to that circuit and stops if the tab closes (`Home.Dispose`).
  Runs that outlive the browser (CI, CLI) need a different host — see `docs/plan.md`.
- The page without `@rendermode InteractiveServer` renders static HTML and buttons silently do nothing.
  This happened once already.
- The cloud agent environment only offers the .NET 10 SDK. Agents verify by building a copy retargeted to
  `net10.0` (see `AGENTS.md`). The committed project stays on `net11.0`.
