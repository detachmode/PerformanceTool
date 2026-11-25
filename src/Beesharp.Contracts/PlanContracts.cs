using Beesharp.Services;

namespace Beesharp.Contracts;

// /api/plan and /api/presets (brain 0050, 0054).

/// <summary>
/// The server's view of the Live plan: the plan being edited, the name of the loaded preset and the plan as last saved
/// (for "unsaved changes" and per-entry "changed"). <see cref="Version"/> grows with every real change.
/// </summary>
/// <param name="Plan">The current in-memory load plan being edited.</param>
/// <param name="PresetName">The name of the currently loaded preset, or an empty string if none.</param>
/// <param name="SavedPlan">The last saved snapshot of the load plan used for dirty comparison.</param>
/// <param name="HasUnsavedChanges"><see langword="true"/> when <see cref="Plan"/> differs from <see cref="SavedPlan"/>.</param>
/// <param name="Version">Monotonically increasing workspace revision number incremented on every meaningful change.</param>
/// <param name="DefaultEnvironment">Optional default environment name persisted with the preset or workspace.</param>
public sealed record PlanWorkspaceState(LoadPlan Plan, string PresetName, LoadPlan SavedPlan, bool HasUnsavedChanges, long Version, string? DefaultEnvironment = null);

/// <summary>A named load plan preset stored on disk.</summary>
/// <param name="Name">Unique preset name.</param>
/// <param name="SavedAt">UTC timestamp when the preset was last saved.</param>
/// <param name="Plan">The load plan definition stored in the preset.</param>
public sealed record Preset(string Name, DateTimeOffset SavedAt, LoadPlan Plan);

/// <summary>Request payload for adding a scenario entry to the live load plan.</summary>
/// <param name="ScenarioId">Identifier of the scenario to add.</param>
public sealed record AddEntryRequest(string ScenarioId);
