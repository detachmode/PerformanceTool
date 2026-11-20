# AGENTS.md

Instructions for coding agents (and humans) working in this repo.

## Read before you change anything
1. **`docs/brain/`** holds the decision records: what we built and why. Read the index in
   `docs/brain/README.md` and the records for the area you're touching **before** making changes.
   Don't contradict a record silently: follow it, or add a new record that supersedes it.
2. **`docs/context.md`** is the domain language. Use its terms (execution, scenario entry, run, …) consistently in
   code, UI and docs.
3. **`docs/plan.md`** is the backlog. Check it before starting new features, and update it when you finish one.

## Record your decisions
When you make a decision someone might later ask "why?" about (architecture, dependency, data format,
trade-off, rejected alternative), add a record to `docs/brain/` using the template in its README and add it
to the index. Commit it in the same change as the code.

## Project
Three projects plus tests (brain 0015), all `net11.0`:
- `src/PerformanceTool.Core` — class library shared by client and server. Keep it free of ASP.NET and browser dependencies.
  - `Services/Scenarios/`: `Scenario<TParameters>` and the built-in scenarios. A new scenario is a parameter class
    plus a scenario class plus `AddScenario<T>()` in **both** `Program.cs` files (client and server). The editor is **generated** from the
    parameter class (`Services/Scenarios/Spec/ParameterSpec.cs`, client `Components/Parameters/`), never hand-written per scenario (brain 0007).
  - `Services/LoadPlan.cs` (the plan, also the preset format), `ExecutionModels.cs` (`ExecutionResult`, `ExecutionActivity`).
- `src/PerformanceTool.Client` — the UI, standalone **Blazor WebAssembly**. Pages `/` (overview) and `/editor`.
  - `Services/IRunSource.cs` is the seam to the data: `FakeRunSource` (in-browser simulation, the default) and `ApiRunSource` (stub, throws until the REST API exists).
    Choose with `"DataSource": "Fake" | "Api"` in `wwwroot/appsettings.json`.
  - `Pages/`, `Layout/` (top bar and the bottom `PlayerBar` with play/pause, stop, run readouts and Logs, brain 0027), `Components/` (`ScenarioCard`, popups `ScenarioDetailDialog` and `LogsDialog`, `VirtualList`, `ActorBars`, `Parameters/`), `wwwroot/app.css` (dark tokens; flat, dark only; colour only means failed = red or slow = amber, brain 0018).
  - **Slow rule (placeholder):** `Services/SlowCallRule.cs` is the one place that defines "slow". **Scale (brain 0021/0022):** `RunStore` keeps tiny actor records for the whole run and calls/logs for at most `RunStoreOptions.MaxDetailedActorsPerScenario` (1500) actors per scenario, dropping successful actors first, then slow, then failed (brain 0028); each distinct error text is stored once in `ErrorDictionary` (`store.Errors`; `ActorRecord`/`FakeCall`/`FakeLogEntry` hold ids, `Error`/`Message`/`Exception` resolve them; `store.HasDetail(seq)` tells whether detail is kept); long lists use `VirtualList`, charts are bounded (`ActorBars`). Debug: open the app with `?simulate=3h` to pre-generate a 3 hour run.
  - **Trimming:** Release publish trims. `PerformanceTool.Core` is a `TrimmerRootAssembly` in the csproj so the reflection-based editor keeps its metadata. Always test the *published* build, not only `dotnet run`.
- `src/PerformanceTool` — ASP.NET Core host. `LoadRunner` (open-model scheduler), `PresetStore` (JSON presets, `Presets:Directory`),
  `GET /api/scenarios`. It serves the client (project reference, static web assets via `MapStaticAssets`, `MapFallbackToFile("index.html")`). No Razor UI of its own.
- `tests/PerformanceTool.Tests` — references Core and the server.

## Build, test and run
```bash
dotnet test
```
Client alone (fake data): `dotnet run --project src/PerformanceTool.Client`. Server plus client:
```bash
dotnet run --project src/PerformanceTool
```
CI: `.github/workflows/ci.yml` runs restore, build and test (Release) on pushes to `main` and on PRs (brain 0014). Keep `dotnet test` green.
If only an older SDK is available (the cloud agent environment has .NET 10), copy `src/`, `tests/` and the
`.slnx` to a scratch folder, change every `net11.0` to `net10.0` there, and build and test the copy. Don't change
the committed `net11.0` targets (also change `Microsoft.AspNetCore.Components.WebAssembly` `11.0.*-*` to `10.0.*` in the copy).

**GitHub Pages:** `.github/workflows/pages.yml` publishes the client (Release, trimmed) on every push to `main` and on manual dispatch, rewrites
`<base href>` to `/PerformanceTool/`, copies `index.html` to `404.html` (deep links like `/editor`) and adds `.nojekyll`. Site: https://detachmode.github.io/PerformanceTool/
(fake data only). The repo setting Pages > Source must be "GitHub Actions". To test locally, publish, apply the same rewrite and serve under a `/PerformanceTool/` path.
**Build info and cache (brain 0025):** the workflow publishes with `-p:BuildSha=${{ github.sha }} -p:BuildTime=<UTC>` (shown by `Layout/BuildStamp.razor`),
writes `build-info.json` (the page fetches it to offer "New version available, reload"), and adds `?v=<sha>` to `app.css` and `favicon.svg`. Blazor fingerprints `_framework/*` and the
JS modules via the importmap in `index.html`. Pages allows no custom HTTP headers. When you add a stylesheet or icon to `index.html`, add it to the `?v=` sed in `pages.yml` too.

## UX bar
The UI must be interactive with a really good UX (brain 0006): every data element can be clicked to inspect
it, used as a filter and isolated. Don't add read-only data views. **Every counter or figure drills down** to the
details behind it (brain 0029), e.g. an errors counter opens the failed actors.

## Screenshots in the repo and in PRs
- Never overwrite or rename existing files in `docs/screenshots/`; earlier PR descriptions link to them.
- Put each set of screenshots in a **new, uniquely named folder**, e.g. `docs/screenshots/2026-11-20-dock-magnify/`.
- Each screenshot folder has a `README.md` gallery that embeds its images (relative paths). GitHub renders it with the
  images inline.
- **The GitHub tool used by agents defuses image embeds *and* markdown links to github.com / raw.githubusercontent.com in
  PR descriptions** (`![..](..)`, `[..](..)` and `<img>` get wrapped in code spans). So PR descriptions give the
  folder's README gallery as a **bare URL on its own line** (GitHub auto-links it), commit-pinned:
  `https://github.com/<owner>/<repo>/blob/<commit-sha>/docs/screenshots/<folder>/README.md`.
- If a raw image URL is needed elsewhere, use a **commit-pinned raw URL**:
  `https://raw.githubusercontent.com/<owner>/<repo>/<commit-sha>/docs/screenshots/...png`, not a branch name.
  Don't use `github.com/.../blob/...?raw=true`: it redirects, and many viewers (apps, notifications) then show only the
  URL instead of the image.

## UI changes need a screenshot
Whenever you change the UI, run the app, drive it with Playwright (a real run, not just the empty page) and
**show the user a screenshot**. Run with `ASPNETCORE_ENVIRONMENT=Development` so static assets resolve.
