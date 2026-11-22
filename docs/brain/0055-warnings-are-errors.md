# 0055. Warnings are errors in every project

- Status: Accepted
- Date: 2026-11-22

## What
`Directory.Build.props` at the repository root sets `TreatWarningsAsErrors` to `true` for every project (user decision).
*Proposed (Claude):* NuGet audit warnings (`NU1901`–`NU1904`, known vulnerable packages) stay warnings
(`WarningsNotAsErrors`), so an advisory published overnight doesn't break every build and CI run at once; they still
show in the build output. Today that is `SQLitePCLRaw.lib.e_sqlite3` (NU1903), which goes away when SQLite is dropped (0054, U2).

## Why
Warnings that nobody fixes pile up and hide the ones that matter (nullable warnings point at real null bugs).
Failing the build keeps the count at zero, also for agents.

## Consequences
- The two existing warnings were fixed (nullable: `ScenarioContext` constructor order, a test).
- New code must build without warnings in Debug, Release and the trimmed client publish.
