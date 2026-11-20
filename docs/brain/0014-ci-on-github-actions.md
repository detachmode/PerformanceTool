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
