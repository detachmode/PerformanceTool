using Beesharp.Services;

namespace Beesharp.Contracts;

// /api/plan and /api/presets (brain 0050, 0054).

/// <summary>
/// The server's view of the Live plan: the plan being edited, the name of the loaded preset and the plan as last saved
/// (for "unsaved changes" and per-entry "changed"). <see cref="Version"/> grows with every real change.
/// </summary>
public sealed record PlanWorkspaceState(LoadPlan Plan, string PresetName, LoadPlan SavedPlan, bool HasUnsavedChanges, long Version);

public sealed record Preset(string Name, DateTimeOffset SavedAt, LoadPlan Plan);

public sealed record AddEntryRequest(string ScenarioId);
