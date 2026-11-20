# 0007. Scenario editor UI is derived from a typed spec in code

- Status: Accepted (requirement, not implemented yet, see `docs/plan.md` → "Typed scenario parameters").
  Extends 0003: replaces its hand-written `ScenarioParameter` list once implemented.
- Date: 2026-11-20

## What
A scenario declares its parameters as a **typed C# definition** (e.g. a parameters record/class), and the
scenario editor UI is **generated from that type**. Adding a scenario, or a property to one, gives the
right editor automatically, with no UI code:

| C# type / annotation | Editor |
|---|---|
| `string` | text input; `[Multiline]` → textarea; `[Url]` → URL input with validation |
| `bool` | toggle switch |
| `enum` | dropdown of enum values (display names via `[Display]`/`[Description]`) |
| `int`, `long`, `double` | number input; `[Range]` → min/max (slider if range is small) |
| `TimeSpan` | duration input (e.g. `500ms`, `2s`) |
| `string[]` / `List<T>` | list editor (add/remove/reorder rows) |
| `Dictionary<string,string>` | key/value editor (e.g. headers) |
| nested record | grouped / collapsible section |

Metadata comes from standard attributes where possible (`[Display(Name, Description, Order, GroupName)]`,
`[Required]`, `[Range]`, `[Url]`, `[DefaultValue]`), plus a few of our own where needed (`[Multiline]`,
`[Secret]` → masked input, not stored in plain presets). Validation reuses the same attributes for the UI,
presets, CLI and CI.

The scenario gets a strongly typed object, e.g.
`class HttpGetScenario : Scenario<HttpGetParameters>` with
`ExecuteAsync(HttpGetParameters p, ScenarioContext ctx, CancellationToken ct)`. No string-keyed lookups.

## Why
- Adding scenarios should be cheap and safe: write a class, get a correct, validated editor for free.
- A typed definition catches mistakes at compile time and gives the editor real types (enum → dropdown,
  bool → toggle) instead of everything being text.
- The same spec can produce a JSON Schema, so the CLI (`perftool scenarios list --json`), presets and
  coding harnesses all describe and validate parameters the same way as the UI (see `docs/plan.md` §2).

## Consequences
- Presets keep storing parameters as JSON. The typed object is (de)serialised with System.Text.Json, and
  defaults fill properties missing from older presets (same compatibility rule as 0003/0005).
- Renaming a property breaks old presets like renaming a scenario `Id` does. Use `[JsonPropertyName]` to
  keep the stored name stable.
- Generate the editor with a reflection-based metadata model built once per scenario type and cached.
  Razor renders that model, so there are no per-scenario Razor files.
- An unknown property type must fail loudly at startup (catalog validation), not render a broken field.
