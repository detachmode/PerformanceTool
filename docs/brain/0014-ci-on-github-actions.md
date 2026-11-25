# 0014. CI on GitHub Actions

- Status: Accepted (user decision: "add a github ci pipeline")
- Date: 2026-11-20

## What
`.github/workflows/ci.yml` runs on every push, pull request and manual dispatch (`workflow_dispatch`). One job on
`ubuntu-latest`: checkout, set up .NET 11, cache NuGet, `dotnet restore` / `build` (Release) / `test` on
`PerformanceTool.slnx`, then publish the TRX results as the `test-results` artifact and a short summary in the
job summary (`.github/scripts/test-summary.sh`, plain shell, no third-party actions).
Runs on the same ref cancel each other (`concurrency`); the token is read-only (`permissions: contents: read`).

## Why
- Build and tests must be checked automatically on every change, also for work done in background agents'
  branches (hence `push` on all branches).
- `.NET 11` may still be a prerelease: `dotnet-version: 11.0.x` with `dotnet-quality: preview` installs the newest
  11.0 build, preview or GA, so the workflow survives the release.
- NuGet is cached with `actions/cache` keyed by the project files (no lock files in the repo).
- Only official `actions/*` actions at pinned major versions: small supply-chain surface. The summary is a few
  lines of shell instead of a marketplace reporter.
- This is CI for this repo itself. CI integration of the tool (brain 0009, 0010) is a separate topic.

## Update (2026-11-20)
CI runs only on **pushes to `main`** and on **pull requests** (build validation), plus manual dispatch. It no
longer runs on pushes to other branches (user decision).

## Update (2026-11-24)
Unified the separate CI, Docker, Pages, and tag-triggered NuGet workflows into one ordered pipeline in
`.github/workflows/ci.yml` (`.github/workflows/nuget.yml` stays callable via `workflow_call` and manual
`workflow_dispatch` for dry-runs/ad-hoc releases):
`build-test` -> (`docker-build` -> `e2e` on amd64 + arm64 -> `docker-push` / `nuget-publish`) and
`build-test` -> (`pages-build` -> `pages-deploy` on `main`).
Nothing deploys to GitHub Pages, GHCR, or NuGet.org unless `build-test` (and `e2e` for Docker/NuGet) passes first.

## Update (2026-11-25)
Added a lightweight `gate` job at the start of `.github/workflows/ci.yml` to deduplicate simultaneous `main` + `v*` tag pushes (e.g. `git push --follow-tags`):
- On a `push` to `refs/heads/main`, `gate` checks via `git ls-remote --tags` whether `github.sha` already has a `v*` tag; if so, it sets `run=false` and skips the duplicate `main` pipeline.
- On a `push` to `refs/tags/v*`, `gate` checks whether `refs/heads/main` points at `github.sha` (`is_main=true`), so the single tag run also publishes the `:latest` Docker tag and deploys GitHub Pages while keeping `docker-push` and `nuget-publish` gated on `e2e`.
