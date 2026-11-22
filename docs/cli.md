# The command-line tool (`beesharp`)

`src/Beesharp.Cli` runs a preset **without the web server and the UI**, for CI pipelines (brain 0054). It uses the same
Engine code as the server: preflight, scenario entries, shutdown program. It prints a summary, saves the run and
exits with an error code when bees failed.

## Run it

```bash
dotnet run --project src/Beesharp.Cli -- run --preset "Black Friday" --env Staging --root ./load-tests
```

or install it as a .NET tool (the command is then `beesharp`):

```bash
dotnet pack src/Beesharp.Cli -c Release -o ./nupkg
dotnet tool install --global Beesharp.Cli --add-source ./nupkg
beesharp run --preset "Black Friday" --env Staging
```

| Option | Meaning |
|---|---|
| `--preset <name\|file>` | The preset's name (from `<root>/presets/`) or the path of a preset `.json` file. Required. |
| `--env <name>` | The environment (`<root>/environments/`) whose `{{Key}}` values are filled in. Unknown keys refuse to start. |
| `--root <folder>` | Holds `presets/`, `environments/`, `scripts/` (with `preflight/`, `shutdown/`) and `data/`. Default: the current directory. |
| `--storage duckdb\|memory` | `duckdb` (default) saves the run to `<root>/data/beesharp.duckdb`; start the server on the same root and the run shows in the UI's history. `memory` saves nothing. |
| `--duration <seconds>` | Run for this long instead of the preset's duration. |
| `--json <file>` | Also write the summary as JSON (camelCase: `bees`, `failedBees`, `entries`, `exitCode`, ...). |
| `--progress <seconds>` | Progress line interval (default 5, `0` = none). |

Output is plain text without colour codes, readable in CI logs.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | All bees succeeded. |
| 1 | At least one bee failed (canceled bees count as failed), the run was stopped with Ctrl+C, or the shutdown program failed. |
| 2 | The run could not run: bad arguments, preset, environment or root folder not found, a script the preset needs does not compile, a scenario / preflight / shutdown program is unknown, an unknown `{{placeholder}}`, the preflight failed. |

Ctrl+C stops the run like Stop in the UI: bees in flight become canceled, the shutdown program still runs, the run is saved.

## GitHub Actions example

```yaml
jobs:
  load-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 11.0.x
          dotnet-quality: preview
      - name: Run the preset
        run: >
          dotnet run --project src/Beesharp.Cli -c Release --
          run --preset "Black Friday" --env Staging --root ./load-tests --json summary.json
      - name: Keep the run
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: beesharp-run
          path: |
            summary.json
            load-tests/data/beesharp.duckdb
```

The step fails the job on exit code 1 or 2. Sample output: `docs/screenshots/2026-11-22-cli/`.
