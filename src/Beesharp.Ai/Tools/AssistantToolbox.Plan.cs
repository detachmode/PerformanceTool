using System.ComponentModel;
using System.Text.Json.Nodes;
using Beesharp.Ai.Contracts;
using Beesharp.Services;

namespace Beesharp.Ai;

// Plan, scenario entries and presets.
public sealed partial class AssistantToolbox
{
    private static readonly ChipLinkDto EditorLink = new("editor");

    /// <summary>Lists all scenarios registered in the catalog (built-in and custom script scenarios).</summary>
    /// <returns>A list of scenario summaries including id, name, description, custom flag, and 1-based declared steps.</returns>
    [AssistantTool("list_scenarios")]
    [Description("Lists the scenarios that can be put in a plan (built-in and custom script scenarios): id, name, declared steps (numbered from 1) and what each does.")]
    public object ListScenarios()
    {
        rec.Read($"Looked at the {catalog.All.Count} scenarios");
        return catalog.All.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            description = s.Description,
            isCustom = catalog.IsCustom(s.Id),
            steps = s.Steps.Count > 0 ? s.Steps.Select((st, i) => new { step = i + 1, name = st.Name }).ToList() : null,
        }).ToList();
    }

    /// <summary>Describes a single scenario including its declared steps and parameter JSON schema.</summary>
    /// <param name="scenarioId">Scenario identifier (for example <c>http-get</c>).</param>
    /// <returns>An object containing scenario metadata, 1-based steps, and parameter JSON schema.</returns>
    [AssistantTool("get_scenario")]
    [Description("Describes one scenario: its description, declared steps (numbered from 1) and the JSON Schema of its parameters (the settings of a scenario entry).")]
    public object GetScenario([Description("Scenario id, e.g. http-get")] string scenarioId)
    {
        var s = catalog.Find(scenarioId) ?? throw new KeyNotFoundException($"There is no scenario '{scenarioId}'. Use list_scenarios.");
        rec.Read($"Looked at scenario {s.Name}");
        return new
        {
            id = s.Id,
            name = s.Name,
            description = s.Description,
            isCustom = catalog.IsCustom(s.Id),
            steps = s.Steps.Count > 0 ? s.Steps.Select((st, i) => new { step = i + 1, name = st.Name }).ToList() : null,
            parameters = s.Spec.ToJsonSchema(),
        };
    }

    /// <summary>Reads the current Live plan with secret parameter values stripped.</summary>
    /// <returns>A model-safe representation of the Live plan, its entries (1-based), and validation problems.</returns>
    [AssistantTool("get_plan")]
    [Description("Reads the Live plan the user sees in the editor: its scenario entries (numbered from 1) with rate, spawning pattern and parameters, plus duration, max in flight, timeout, preflight and any problems that would stop a run. Secret parameter values are left out.")]
    public object GetPlan()
    {
        var state = plan.Get();
        rec.Read($"Read the plan ({state.Plan.Scenarios.Count} scenario entries)", EditorLink);
        return PlanFormat.View(state, catalog);
    }

    /// <summary>Adds a new scenario entry to the Live plan and optionally configures its label, rate, and parameters.</summary>
    /// <param name="scenarioId">Identifier of the scenario to add.</param>
    /// <param name="label">Optional display label for the new entry.</param>
    /// <param name="beesPerMinute">Optional linear spawning rate in bees per minute.</param>
    /// <param name="parameters">Optional JSON object of scenario parameters to merge into defaults.</param>
    /// <returns>An object containing the added entry view, normalization warnings, and plan validation problems.</returns>
    [AssistantTool("add_scenario_entry")]
    [Description("Adds a scenario entry to the plan. Optionally sets its label, rate and parameters right away. Parameters are merged into the defaults; use get_scenario to see which exist.")]
    public object AddScenarioEntry(
        [Description("Scenario id, e.g. http-get")] string scenarioId,
        [Description("Name of this entry in the plan, e.g. Checkout")] string? label = null,
        [Description("Linear spawning: new bees per minute")] double? beesPerMinute = null,
        [Description("Scenario parameters to set, as a JSON object, e.g. {\"url\":\"https://example.com\"}")] JsonObject? parameters = null)
    {
        Touch();
        var index = plan.AddEntry(scenarioId);
        var warnings = new List<string>();
        if (label is not null || beesPerMinute is not null || parameters is not null)
            plan.Update(p => { ApplyEntryEdit(p.Scenarios[index], label, null, beesPerMinute, null, null, parameters, warnings); return true; });
        var state = plan.Get();
        var entry = state.Plan.Scenarios[index];
        rec.Change(ChangedAreas.Plan, $"Added {PlanFormat.Label(entry)} to the plan (entry {index + 1})", EditorLink);
        return new { added = PlanFormat.EntryView(entry, index, catalog), warnings, problems = state.Plan.Validate(catalog) };
    }

    /// <summary>Updates settings or parameters of an existing scenario entry in the Live plan.</summary>
    /// <param name="entry">1-based entry number to update.</param>
    /// <param name="label">Optional new display label.</param>
    /// <param name="pattern">Optional spawning pattern (<c>linear</c> or <c>bursts</c>).</param>
    /// <param name="beesPerMinute">Optional linear spawning rate in bees per minute.</param>
    /// <param name="beesPerBurst">Optional number of bees started per burst.</param>
    /// <param name="burstsPerMinute">Optional burst frequency in bursts per minute.</param>
    /// <param name="parameters">Optional JSON object of parameter updates merged into the current parameters.</param>
    /// <returns>An object containing the updated entry view, normalization warnings, and plan validation problems.</returns>
    [AssistantTool("update_scenario_entry")]
    [Description("Changes a scenario entry of the plan: label, spawning pattern (linear or bursts), rate, burst size and rate, and/or scenario parameters (merged into the current ones). Only the given settings change.")]
    public object UpdateScenarioEntry(
        [Description("Entry number, starting at 1")] int entry,
        [Description("New name of the entry")] string? label = null,
        [Description("linear or bursts")] string? pattern = null,
        [Description("Linear spawning: new bees per minute")] double? beesPerMinute = null,
        [Description("Bursts: bees started together by each burst")] int? beesPerBurst = null,
        [Description("Bursts: bursts per minute")] double? burstsPerMinute = null,
        [Description("Scenario parameters to change, as a JSON object; other parameters keep their value")] JsonObject? parameters = null)
    {
        var index = ToIndex(entry, "Entry");
        Touch();
        var warnings = new List<string>();
        var changes = new List<(string What, string? To)>();
        plan.Update(p =>
        {
            if (index >= p.Scenarios.Count) throw new ArgumentException($"There is no entry {entry}; the plan has {p.Scenarios.Count}.");
            changes.AddRange(ApplyEntryEdit(p.Scenarios[index], label, pattern, beesPerMinute, beesPerBurst, burstsPerMinute, parameters, warnings));
            return true;
        });
        var state = plan.Get();
        var current = state.Plan.Scenarios[index];
        rec.Change(ChangedAreas.Plan, ChangeSummary(changes, PlanFormat.Label(current)), EditorLink);
        return new { entry = PlanFormat.EntryView(current, index, catalog), warnings, problems = state.Plan.Validate(catalog) };
    }

    /// <summary>Removes a scenario entry from the Live plan.</summary>
    /// <param name="entry">1-based entry number to remove.</param>
    /// <returns>An object confirming the removed entry label and remaining entry count.</returns>
    [AssistantTool("remove_scenario_entry")]
    [Description("Removes a scenario entry from the plan. Later entries move up by one number.")]
    public object RemoveScenarioEntry([Description("Entry number, starting at 1")] int entry)
    {
        var index = ToIndex(entry, "Entry");
        Touch();
        var before = plan.Get().Plan;
        if (index >= before.Scenarios.Count) throw new ArgumentException($"There is no entry {entry}; the plan has {before.Scenarios.Count}.");
        var label = PlanFormat.Label(before.Scenarios[index]);
        var state = plan.RemoveEntry(index);
        rec.Change(ChangedAreas.Plan, $"Removed {label} from the plan", EditorLink);
        return new { removed = label, entriesLeft = state.Plan.Scenarios.Count };
    }

    /// <summary>Updates global settings of the Live plan such as duration, concurrency limit, timeout, and preflight script.</summary>
    /// <param name="durationSeconds">Optional run duration in seconds.</param>
    /// <param name="maxInFlight">Optional maximum concurrent bees in flight.</param>
    /// <param name="timeoutSeconds">Optional per-bee timeout in seconds.</param>
    /// <param name="preflightScriptId">Optional preflight script id, or empty string to clear the preflight.</param>
    /// <returns>The updated Live plan view.</returns>
    [AssistantTool("set_plan_settings")]
    [Description("Changes the settings of the whole plan: duration of the run, max bees in flight, timeout of one bee, and the preflight script to run first. Only the given settings change.")]
    public object SetPlanSettings(
        [Description("Duration of the run in seconds (1 to 86400)")] int? durationSeconds = null,
        [Description("Most bees in flight at once (1 to 1000)")] int? maxInFlight = null,
        [Description("Timeout of one bee in seconds (1 to 300); a bee that takes longer fails")] int? timeoutSeconds = null,
        [Description("Id of a preflight script to run before the scenarios start; an empty string removes the preflight")] string? preflightScriptId = null)
    {
        Touch();
        var changes = new List<string>();
        var state = plan.Update(p =>
        {
            if (durationSeconds is { } d && d != p.DurationSeconds) { p.DurationSeconds = d; changes.Add($"duration to {d} s"); }
            if (maxInFlight is { } m && m != p.MaxConcurrency) { p.MaxConcurrency = m; changes.Add($"max in flight to {m}"); }
            if (timeoutSeconds is { } t && t != p.TimeoutSeconds) { p.TimeoutSeconds = t; changes.Add($"timeout to {t} s"); }
            if (preflightScriptId is not null)
            {
                var id = string.IsNullOrWhiteSpace(preflightScriptId) ? null : preflightScriptId.Trim();
                if (id is not null && scripts.GetPreflight(id) is null) throw new KeyNotFoundException($"There is no preflight '{id}'. Use list_preflights.");
                if (id != p.PreflightScriptId) { p.PreflightScriptId = id; changes.Add(id is null ? "preflight removed" : $"preflight to {id}"); }
            }
            return changes.Count > 0;
        });
        rec.Change(ChangedAreas.Plan, changes.Count == 0 ? "No plan setting needed a change" : $"Changed {string.Join(", ", changes)}", EditorLink);
        return PlanFormat.View(state, catalog);
    }

    // ---------- presets ----------

    /// <summary>Lists all saved plan presets and reports which preset is currently loaded.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An object containing the loaded preset name and preset summaries.</returns>
    [AssistantTool("list_presets")]
    [Description("Lists the saved presets (named plans), and which one is loaded.")]
    public async Task<object> ListPresets(CancellationToken ct)
    {
        var presets = await plan.ListPresetsAsync(ct);
        rec.Read($"Looked at the {presets.Count} presets");
        return new { loaded = plan.Get().PresetName, presets = presets.Select(p => new { name = p.Name, entries = p.Entries, durationSeconds = p.DurationSeconds, savedAt = p.SavedAt }).ToList() };
    }

    /// <summary>Saves the current Live plan as a named preset and marks it as loaded.</summary>
    /// <param name="name">Preset name to save under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An object confirming the saved preset name.</returns>
    [AssistantTool("save_preset")]
    [Description("Saves the current plan as a preset with this name (replacing a preset of the same name) and makes it the loaded one.")]
    public async Task<object> SavePreset([Description("Preset name")] string name, CancellationToken ct)
    {
        await plan.SavePresetAsync(name, ct);
        rec.Change(ChangedAreas.Presets, $"Saved the plan as preset “{name.Trim()}”", EditorLink);
        return new { saved = name.Trim() };
    }

    /// <summary>Loads a saved preset into the Live plan, replacing the current editor state.</summary>
    /// <param name="name">Name of the preset to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated Live plan view after loading the preset.</returns>
    [AssistantTool("load_preset")]
    [Description("Loads a saved preset into the plan, replacing the plan in the editor.")]
    public async Task<object> LoadPreset([Description("Preset name")] string name, CancellationToken ct)
    {
        Touch();
        if (!await plan.LoadPresetAsync(name, ct)) throw new KeyNotFoundException($"There is no preset '{name}'. Use list_presets.");
        rec.Change(ChangedAreas.Plan, $"Loaded preset “{name.Trim()}”", EditorLink);
        return PlanFormat.View(plan.Get(), catalog);
    }

    /// <summary>Permanently deletes a saved preset without modifying the current Live plan.</summary>
    /// <param name="name">Name of the preset to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An object confirming the deleted preset name.</returns>
    [AssistantTool("delete_preset")]
    [Description("Deletes a saved preset for good. The plan in the editor stays as it is.")]
    public async Task<object> DeletePreset([Description("Preset name")] string name, CancellationToken ct)
    {
        if (!await plan.DeletePresetAsync(name, ct)) throw new KeyNotFoundException($"There is no preset '{name}'. Use list_presets.");
        rec.Change(ChangedAreas.Presets, $"Deleted preset “{name.Trim()}”");
        return new { deleted = name.Trim() };
    }

    // ---------- internals ----------

    private static string ChangeSummary(List<(string What, string? To)> changes, string label) => changes.Count switch
    {
        0 => $"Nothing to change on {label}",
        1 when changes[0].To is { } to => $"Changed {changes[0].What} of {label} to {to}",
        _ => $"Changed {string.Join(", ", changes.Select(c => c.What))} of {label}",
    };

    private void Touch() => rec.PlanBefore ??= plan.Get().Plan;

    /// <summary>Applies the given settings to an entry and returns short phrases of what changed ("rate to 60 bees/min").</summary>
    private List<(string What, string? To)> ApplyEntryEdit(ScenarioRunConfig c, string? label, string? pattern, double? beesPerMinute, int? beesPerBurst, double? burstsPerMinute,
        JsonObject? parameters, List<string> warnings)
    {
        var changes = new List<(string What, string? To)>();
        if (!string.IsNullOrWhiteSpace(label) && label.Trim() != c.Label) { c.Label = label.Trim(); changes.Add(("name", $"“{c.Label}”")); }
        if (pattern is not null)
        {
            var parsed = pattern.Trim().ToLowerInvariant() switch
            {
                "linear" => SpawningPattern.Linear,
                "bursts" or "burst" => SpawningPattern.Bursts,
                _ => throw new ArgumentException("pattern must be linear or bursts."),
            };
            if (parsed != c.Pattern) { c.Pattern = parsed; changes.Add(("spawning pattern", parsed.ToString().ToLowerInvariant())); }
        }
        if (beesPerMinute is { } r && r != c.RatePerMinute) { c.RatePerMinute = r; changes.Add(("rate", $"{PlanFormat.Rate(r)} bees/min")); }
        if (beesPerBurst is { } b && b != c.BeesPerBurst) { c.BeesPerBurst = b; changes.Add(("burst size", b.ToString())); }
        if (burstsPerMinute is { } bm && bm != c.BurstsPerMinute) { c.BurstsPerMinute = bm; changes.Add(("bursts per minute", PlanFormat.Rate(bm))); }
        if (parameters is { Count: > 0 })
        {
            var scenario = catalog.Find(c.ScenarioId);
            var known = scenario?.Spec.Fields.Select(f => f.JsonName).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var merged = (JsonObject)c.Parameters.DeepClone();
            var changed = new List<string>();
            foreach (var (key, value) in parameters)
            {
                var name = known.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (name is null) { warnings.Add($"Parameter '{key}' does not exist on {c.ScenarioId} and was ignored."); continue; }
                if (merged[name]?.ToJsonString() != value?.ToJsonString()) changed.Add(name);
                merged[name] = value?.DeepClone();
            }
            if (scenario is not null)
            {
                var problems = new List<string>();
                merged = scenario.Spec.Normalize(merged, problems);
                warnings.AddRange(problems);
            }
            c.Parameters = merged;
            if (changed.Count > 0) changes.Add((changed.Count == 1 ? changed[0] : $"{changed.Count} parameters", null));
        }
        return changes;
    }
}
