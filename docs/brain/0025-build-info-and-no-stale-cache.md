# 0025. Show build info; don't serve stale cached builds

- Status: Accepted (user request); techniques are implementation choices
- Date: 2026-11-20

## What
- The page shows **build info**: the **commit** it was built from and the **date of the deploy**. Keep it small and calm
  (0016), e.g. in the top bar or footer, linking the commit to GitHub.
- The browser must **not keep using an old cached build** after a new deploy.

## Why
The user tests the UX on GitHub Pages after each merge. They need to see which build they're looking at, and they
need to get the newest one without fighting the browser cache.

## Consequences
- The Pages workflow (and local builds) inject the commit SHA and the build/deploy timestamp at publish time.
- Static assets get cache-busted (fingerprinted file names or version query strings). GitHub Pages doesn't allow
  custom HTTP cache headers, so `index.html` itself relies on GitHub's short cache plus in-page measures.

## Implementation notes (2026-11-20)
**Build info.** `PerformanceTool.Client.csproj` bakes `BuildSha` and `BuildTime` (`-p:BuildSha=... -p:BuildTime=...`) into the
assembly as `AssemblyMetadata`; `Services/BuildInfo.cs` reads them (works in the trimmed Release publish, verified). `Layout/BuildStamp.razor`
shows `build 3f9a1c2 · 20 Nov 2026 15:30 UTC` muted at the far right of the top bar; the SHA links to the full commit on GitHub.
Without a SHA (local build) it shows `build local · <build time>`. `pages.yml` passes `${{ github.sha }}` and the run's UTC time.

**Cache busting.**
- Blazor's content hashing (`<OverrideHtmlAssetPlaceholders>`, `#[.{fingerprint}]` and `<script type="importmap">` in `index.html`) covers
  `blazor.webassembly.js`, the `_framework` files and, through a `StaticWebAssetFingerprintPattern` for `*.js`, `dock-magnify.js` (importmap entry).
  A new deploy with changed content has new names, so old files cannot be reused.
- The placeholders did **not** resolve for this project's own `wwwroot/app.css` and `favicon.svg` (tried renaming them and a fingerprint pattern: the file
  got a hash name but `index.html` kept the plain name), so the Pages workflow appends `?v=<full sha>` to them. Local builds keep the plain names.
- `index.html` has `Cache-Control`, `Pragma` and `Expires` metas. Browsers largely ignore these for the document itself; they are best effort.
- `build-info.json` (`{"sha","time"}`) is written next to `index.html` by the workflow. On load the page fetches it with `cache: no-store` (a unique query string too)
  and, if the SHA differs from the running build, shows "New version available, reload" next to the build info. Silent when the file is missing (local).

**Limits.** GitHub Pages sets its own headers (`Cache-Control: max-age=600`) and allows none of ours, so `index.html` itself can be served from the browser cache
for up to about 10 minutes after a deploy. The hint above is the answer to that: the stale page tells the user a newer build exists. The `<meta>` tags do not change HTTP caching.
A hard reload always works.
