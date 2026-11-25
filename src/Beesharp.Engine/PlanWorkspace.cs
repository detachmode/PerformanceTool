using System.Text.Json;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services;

/// <summary>
/// Owns the editable plan and the presets in Live mode (brain 0047/0050), so the UI and the AI assistant share one source of truth.
/// Last writer wins: the editor pushes the whole plan, the assistant changes it through the same methods, and everybody
/// finds out about changes through <see cref="Version"/> (the client polls it) or <see cref="Changed"/> (in-process).
/// </summary>
public sealed class PlanWorkspace
{
    public const string DefaultPresetName = "Live plan";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock sync = new();
    private readonly ScenarioCatalog catalog;
    private readonly PresetStore presets;
    private readonly Dictionary<string, Preset> codePresets = new(StringComparer.OrdinalIgnoreCase);
    private LoadPlan plan = NewPlan();
    private LoadPlan saved = NewPlan();
    private string presetName = DefaultPresetName;
    private string? defaultEnvironment;
    private long version = 1;

    public PlanWorkspace(ScenarioCatalog catalog, PresetStore presets, IEnumerable<Preset>? codePresets = null)
    {
        this.catalog = catalog;
        this.presets = presets;
        if (codePresets is not null) RegisterCodePresets(codePresets);
    }

    /// <summary>Raised after every real change (outside the lock). Handlers must not block.</summary>
    public event Action<PlanWorkspaceState>? Changed;

    public long Version { get { lock (sync) return version; } }

    /// <summary>Default environment name configured at startup (brain 0058). Used when a run starts without an explicit environment.</summary>
    public string? DefaultEnvironment { get { lock (sync) return defaultEnvironment; } }

    public void SetDefaultEnvironment(string? name)
    {
        lock (sync) defaultEnvironment = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>Registers code-defined presets (brain 0058). Disk presets with the same name override these if the user saves edits.</summary>
    public void RegisterCodePresets(IEnumerable<Preset> presetsToRegister)
    {
        ArgumentNullException.ThrowIfNull(presetsToRegister);
        lock (sync)
        {
            foreach (var preset in presetsToRegister)
            {
                var name = preset.Name.Trim();
                if (name.Length == 0) continue;
                codePresets[name] = new Preset(name, preset.SavedAt, preset.Plan.Clone());
            }
        }
    }

    public void RegisterCodePreset(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        RegisterCodePresets([preset]);
    }

    public PlanWorkspaceState GetState() { lock (sync) return Snapshot(); }

    /// <summary>The plan to start a run with (a copy).</summary>
    public LoadPlan GetPlan() { lock (sync) return plan.Clone(); }

    /// <summary>Replaces the whole plan (what the editor pushes). Nothing changes, and the version stays, when the plan is equal.</summary>
    public PlanWorkspaceState SetPlan(LoadPlan newPlan)
    {
        ArgumentNullException.ThrowIfNull(newPlan);
        return Mutate(() =>
        {
            var next = newPlan.Clone();
            if (Fingerprint(next) == Fingerprint(plan)) return false;
            plan = next;
            return true;
        });
    }

    /// <summary>Changes the plan in place under the lock (for the assistant's edits). Return false when nothing changed.</summary>
    public PlanWorkspaceState Update(Func<LoadPlan, bool> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Mutate(() =>
        {
            var copy = plan.Clone();
            if (!edit(copy)) return false;
            if (Fingerprint(copy) == Fingerprint(plan)) return false;
            plan = copy;
            return true;
        });
    }

    /// <summary>Adds an entry for a scenario with its default parameters. Returns the new entry's index.</summary>
    public int AddEntry(string scenarioId)
    {
        var scenario = catalog.Find(scenarioId) ?? throw new ArgumentException($"Unknown scenario '{scenarioId}'.", nameof(scenarioId));
        var index = -1;
        Mutate(() =>
        {
            var config = ScenarioRunConfig.CreateFor(scenario);
            var same = plan.Scenarios.Count(s => s.ScenarioId == scenario.Id);
            config.Label = same == 0 ? scenario.Name : $"{scenario.Name} #{same + 1}";
            config.RatePerMinute = 30;
            plan = plan.Clone();
            plan.Scenarios.Add(config);
            index = plan.Scenarios.Count - 1;
            return true;
        });
        return index;
    }

    public PlanWorkspaceState RemoveEntry(int index) => Mutate(() =>
    {
        if (index < 0 || index >= plan.Scenarios.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"There is no scenario entry {index + 1}.");
        plan = plan.Clone();
        plan.Scenarios.RemoveAt(index);
        return true;
    });

    /// <summary>Seeds an empty workspace (for example with the plan of the run restored at startup). Does nothing when the plan already has entries.</summary>
    public void SeedIfEmpty(LoadPlan seed)
    {
        Mutate(() =>
        {
            if (plan.Scenarios.Count > 0 || seed.Scenarios.Count == 0) return false;
            plan = seed.Clone();
            saved = seed.Clone();
            return true;
        });
    }

    /// <summary>
    /// Applies startup default plan configuration (brain 0058): sets <see cref="DefaultEnvironment"/> when provided,
    /// then loads <see cref="DefaultPlanOptions.DefaultPreset"/> or seeds from <see cref="DefaultPlanOptions.Scenarios"/>
    /// when the workspace is empty (or <see cref="DefaultPlanOptions.ForceDefault"/> is true).
    /// Returns true if a plan was applied.
    /// </summary>
    public async Task<bool> ApplyDefaultPlanAsync(DefaultPlanOptions? options, CancellationToken ct = default)
    {
        if (options is null) return false;

        if (!string.IsNullOrWhiteSpace(options.DefaultEnvironment))
            SetDefaultEnvironment(options.DefaultEnvironment);

        bool isEmpty;
        lock (sync) isEmpty = plan.Scenarios.Count == 0;
        if (!isEmpty && !options.ForceDefault) return false;

        if (!string.IsNullOrWhiteSpace(options.DefaultPreset))
        {
            if (await LoadPresetAsync(options.DefaultPreset, ct) is not null)
                return true;
        }

        if (options.Scenarios is { Count: > 0 })
        {
            var built = BuildInlinePlan(options);
            var applied = false;
            Mutate(() =>
            {
                if (!options.ForceDefault && plan.Scenarios.Count > 0) return false;
                plan = built;
                saved = built.Clone();
                presetName = DefaultPresetName;
                applied = true;
                return true;
            });
            return applied;
        }

        return false;
    }

    private LoadPlan BuildInlinePlan(DefaultPlanOptions options)
    {
        var built = new LoadPlan
        {
            DurationSeconds = options.DurationSeconds ?? 30,
            MaxConcurrency = options.MaxConcurrency ?? 50,
            TimeoutSeconds = options.TimeoutSeconds ?? 15,
            PreflightScriptId = options.PreflightScriptId,
            ShutdownScriptId = options.ShutdownScriptId,
        };

        foreach (var entry in options.Scenarios!)
        {
            if (string.IsNullOrWhiteSpace(entry.ScenarioId)) continue;
            var scenario = catalog.Find(entry.ScenarioId);
            var resolvedId = scenario?.Id ?? entry.ScenarioId.Trim();
            var defaultName = scenario?.Name ?? resolvedId;
            var same = built.Scenarios.Count(s => string.Equals(s.ScenarioId, resolvedId, StringComparison.OrdinalIgnoreCase));
            var label = !string.IsNullOrWhiteSpace(entry.Label)
                ? entry.Label.Trim()
                : (same == 0 ? defaultName : $"{defaultName} #{same + 1}");

            var config = new ScenarioRunConfig
            {
                ScenarioId = resolvedId,
                Label = label,
                RatePerMinute = entry.RatePerMinute ?? 60,
                Pattern = entry.Pattern ?? SpawningPattern.Linear,
                BeesPerBurst = entry.BeesPerBurst ?? 10,
                BurstsPerMinute = entry.BurstsPerMinute ?? 4,
                MaxInFlight = entry.MaxInFlight,
                Parameters = entry.Parameters is not null
                    ? (System.Text.Json.Nodes.JsonObject)entry.Parameters.DeepClone()
                    : scenario?.Spec.DefaultJson() ?? new System.Text.Json.Nodes.JsonObject(),
            };
            if (scenario is not null) config.Normalize(scenario);
            built.Scenarios.Add(config);
        }

        return built;
    }

    // ---------- presets ----------

    public async Task<IReadOnlyList<Preset>> ListPresetsAsync(CancellationToken ct = default)
    {
        var disk = await presets.ListAsync(ct);
        var merged = new Dictionary<string, Preset>(StringComparer.OrdinalIgnoreCase);
        lock (sync)
        {
            foreach (var (name, preset) in codePresets)
                merged[name] = new Preset(preset.Name, preset.SavedAt, preset.Plan.Clone());
        }
        // Disk presets override code presets with the same name (brain 0058).
        foreach (var preset in disk)
            merged[preset.Name] = preset;

        return merged.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Saves a plan (the current one when <paramref name="source"/> is null) under <paramref name="name"/> and makes it the current preset.</summary>
    public async Task<PlanWorkspaceState> SavePresetAsync(string name, LoadPlan? source = null, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Preset name is required.", nameof(name));
        LoadPlan toSave;
        lock (sync) toSave = (source ?? plan).Clone();
        await presets.SaveAsync(name, toSave, ct);
        return Mutate(() =>
        {
            plan = toSave.Clone();
            saved = toSave.Clone();
            presetName = name;
            return true;
        });
    }

    /// <summary>Loads a preset into the workspace. Returns null when there is no preset with that name.</summary>
    public async Task<PlanWorkspaceState?> LoadPresetAsync(string name, CancellationToken ct = default)
    {
        var preset = (await ListPresetsAsync(ct)).FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null) return null;
        var loaded = preset.Plan.Clone();
        foreach (var entry in loaded.Scenarios)
            if (catalog.Find(entry.ScenarioId) is { } scenario) entry.Normalize(scenario);
        return Mutate(() =>
        {
            plan = loaded;
            saved = loaded.Clone();
            presetName = preset.Name;
            return true;
        });
    }

    public async Task<bool> DeletePresetAsync(string name, CancellationToken ct = default)
    {
        var existing = (await presets.ListAsync(ct)).FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is null) return false;
        await presets.DeleteAsync(existing.Name, ct);
        Mutate(() => true); // bumps the version so open editors refresh their preset list
        return true;
    }

    // ---------- internals ----------

    private PlanWorkspaceState Mutate(Func<bool> change)
    {
        PlanWorkspaceState state;
        bool changed;
        lock (sync)
        {
            changed = change();
            if (changed) version++;
            state = Snapshot();
        }
        if (changed) Changed?.Invoke(state);
        return state;
    }

    private PlanWorkspaceState Snapshot() =>
        new(plan.Clone(), presetName, saved.Clone(), Fingerprint(plan) != Fingerprint(saved), version, defaultEnvironment);

    private static LoadPlan NewPlan() => new() { DurationSeconds = 30, MaxConcurrency = 50, TimeoutSeconds = 15 };

    private static string Fingerprint(LoadPlan p) => JsonSerializer.Serialize(p, Json);
}
