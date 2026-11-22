using System.Text.Json;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services;

/// <summary>
/// The server's view of the Live plan: the plan being edited, the name of the loaded preset and the plan as last saved
/// (for "unsaved changes" and per-entry "changed"). <see cref="Version"/> grows with every real change.
/// </summary>
public sealed record PlanWorkspaceState(LoadPlan Plan, string PresetName, LoadPlan SavedPlan, bool HasUnsavedChanges, long Version);

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
    private LoadPlan plan = NewPlan();
    private LoadPlan saved = NewPlan();
    private string presetName = DefaultPresetName;
    private long version = 1;

    public PlanWorkspace(ScenarioCatalog catalog, PresetStore presets)
    {
        this.catalog = catalog;
        this.presets = presets;
    }

    /// <summary>Raised after every real change (outside the lock). Handlers must not block.</summary>
    public event Action<PlanWorkspaceState>? Changed;

    public long Version { get { lock (sync) return version; } }

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
            if (plan.Scenarios.Count >= LoadPlan.MaxEntries)
                throw new InvalidOperationException($"At most {LoadPlan.MaxEntries} scenario entries per plan.");
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

    // ---------- presets ----------

    public Task<IReadOnlyList<Preset>> ListPresetsAsync(CancellationToken ct = default) => presets.ListAsync(ct);

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
        var preset = (await presets.ListAsync(ct)).FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
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
        new(plan.Clone(), presetName, saved.Clone(), Fingerprint(plan) != Fingerprint(saved), version);

    private static LoadPlan NewPlan() => new() { DurationSeconds = 30, MaxConcurrency = 50, TimeoutSeconds = 15 };

    private static string Fingerprint(LoadPlan p) => JsonSerializer.Serialize(p, Json);
}
