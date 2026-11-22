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
- **.NET 11 reports more than the .NET 10 agent copy**, so CI can fail where the local copy passed. Found on the first CI run:
  NU1510 (Core's `Microsoft.Extensions.*.Abstractions` references are provided by .NET 11; they are now conditional on a
  target below 11.0, without a literal `net11.0` so the copy's rewrite keeps them), BL0016 (a JS interop call must have its
  own try/catch) and BL0012 (a `StateHasChanged()` at the end of an event handler is unnecessary). Watch CI after pushing.
