# Beesharp CLI (`beesharp`)

Command-line tool for running [Beesharp](https://github.com/detachmode/PerformanceTool) load-test presets headlessly in CI and inspecting exported run bundles or live Beesharp servers.

## Install

Install as a global .NET tool:

```bash
dotnet tool install --global Beesharp.Cli
```

Update to the latest version:

```bash
dotnet tool update --global Beesharp.Cli
```

This registers the `beesharp` command on your `PATH` (configured via `<ToolCommandName>beesharp</ToolCommandName>`).

## Quick start

Run a preset headlessly:

```bash
beesharp run --preset "Black Friday" --env Staging --root ./load-tests --export run.zip
```

Inspect an exported `.zip`, `.duckdb`, or `.json` run bundle offline:

```bash
beesharp runs summary run.zip
beesharp runs errors run.zip
beesharp runs calls run.zip --slow
beesharp runs logs run.zip --level warn,error
beesharp runs bee run.zip B1
beesharp runs compare baseline.zip candidate.zip
```

Query a running Beesharp server:

```bash
export BEESHARP_URL=http://localhost:5281
beesharp runs list
beesharp runs summary latest
```

See the full CLI reference in [docs/cli.md](https://github.com/detachmode/PerformanceTool/blob/main/docs/cli.md).
