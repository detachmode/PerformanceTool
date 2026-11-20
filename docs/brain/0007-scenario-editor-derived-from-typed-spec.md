# 0007. Scenario editor UI is derived from a typed spec in code

- Status: Accepted, implemented. Replaces the hand-written `ScenarioParameter` list from 0003.
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
| `int`, `long`, `double`, `decimal` (also nullable) | number input; `[Range]` → min/max; integer range ≤ 20 → slider |
| `TimeSpan` | duration input (e.g. `500ms`, `2s`) |
| `string[]` / `List<T>` | list editor (add/remove/reorder rows) |
| `Dictionary<string,string>` | key/value editor (e.g. headers) |
| nested class (public parameterless ctor) | collapsible group |

Metadata comes from standard attributes (`[Display(Name, Description, Prompt, Order)]`, `[Required]`,
`[Range]`, `[MinLength]`). Defaults are the property initialisers. Our own attributes are in
`Services/Scenarios/Spec/Attributes.cs`: `[HttpUrl]` (URL input and validation, also per list item),
`[Multiline]` and `[Secret]` (masked input; stripped from presets unless it is a `${env:NAME}` reference, which
is resolved when the run starts). Validation reuses the same attributes for the UI,
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

## Implementation notes
- `ParameterSpec` (`Services/Scenarios/Spec/ParameterSpec.cs`) is the reflection model, cached per type. It also
  binds, normalises, validates, strips/resolves secrets and exports JSON Schema.
- Values are stored as a `JsonObject` in `ScenarioRunConfig.Parameters`, which is the preset format. The editor
  edits that object in place, and the runner binds it to the typed class once per run.
- Binding is lenient: legacy shapes from string-only presets are converted (multiline string → list,
  `Name: value` lines → dictionary, case-insensitive enum names, durations like `250ms`). Unreadable values
  reset to the default and are reported in the "preset loaded" message.
- Editor components: `Components/Parameters/` (`ParameterEditor` switches on `FieldKind`; `ListEditor`,
  `KeyValueEditor`, `DurationInput`). Supporting a new type means a new `FieldKind`, a case in `ParameterSpec`
  and a case in `ParameterEditor`. Never per-scenario markup.
- `GET /api/scenarios` returns every scenario with its parameter JSON Schema (including an `x-editor` hint).
- The app validates all specs at startup (`ScenarioCatalog` is resolved in `Program.cs`), so an unsupported
  type stops the app with a message that names the property.
- Covered by `tests/PerformanceTool.Tests/ParameterSpecTests.cs` and `LoadPlanTests.cs` (legacy preset).
