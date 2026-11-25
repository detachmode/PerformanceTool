using Beesharp.Ai;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.AiAdapters;

// Adapters of the assistant's ports onto the host's own services (brain 0047/0051). They are thin: mapping and a few guard rails, no logic of their own.

/// <summary>
/// Adapts <see cref="PlanWorkspace"/> to <see cref="IPlanWorkspacePort"/> for the AI assistant,
/// sharing the same live plan state used by the REST API and the editor.
/// </summary>
/// <param name="workspace">The live plan workspace.</param>
public sealed class PlanWorkspaceAdapter(PlanWorkspace workspace) : IPlanWorkspacePort
{
    private static PlanState Map(PlanWorkspaceState s) => new(s.Plan, s.PresetName, s.SavedPlan, s.HasUnsavedChanges, s.Version);

    /// <summary>Gets the current live plan workspace state.</summary>
    /// <returns>The current <see cref="PlanState"/> snapshot.</returns>
    public PlanState Get() => Map(workspace.GetState());

    /// <summary>Applies an edit callback to a copy of the live plan under the workspace lock.</summary>
    /// <param name="edit">A mutation function that returns <see langword="true"/> if the plan changed, or <see langword="false"/> if nothing changed.</param>
    /// <returns>The resulting <see cref="PlanState"/> snapshot.</returns>
    public PlanState Update(Func<LoadPlan, bool> edit) => Map(workspace.Update(edit));

    /// <summary>Appends a new scenario entry with default parameters to the live plan.</summary>
    /// <param name="scenarioId">The identifier of the scenario to add.</param>
    /// <returns>The 0-based index of the newly added scenario entry.</returns>
    public int AddEntry(string scenarioId) => workspace.AddEntry(scenarioId);

    /// <summary>Removes a scenario entry from the live plan by its 0-based index.</summary>
    /// <param name="index">The 0-based index of the entry to remove.</param>
    /// <returns>The updated <see cref="PlanState"/> snapshot.</returns>
    public PlanState RemoveEntry(int index) => Map(workspace.RemoveEntry(index));

    /// <summary>Lists all saved plan presets.</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A read-only list of saved preset summaries.</returns>
    public async Task<IReadOnlyList<PresetInfo>> ListPresetsAsync(CancellationToken ct) =>
        (await workspace.ListPresetsAsync(ct)).Select(p => new PresetInfo(p.Name, p.SavedAt, p.Plan.Scenarios.Count, p.Plan.DurationSeconds)).ToList();

    /// <summary>Saves the current live plan as a named preset.</summary>
    /// <param name="name">The preset name to save under.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    public Task SavePresetAsync(string name, CancellationToken ct) => workspace.SavePresetAsync(name, null, ct);

    /// <summary>Loads a named preset into the live plan workspace.</summary>
    /// <param name="name">The preset name to load.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if the preset existed and was loaded; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> LoadPresetAsync(string name, CancellationToken ct) => await workspace.LoadPresetAsync(name, ct) is not null;

    /// <summary>Deletes a named preset from the store.</summary>
    /// <param name="name">The preset name to delete.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if the preset existed and was deleted; otherwise, <see langword="false"/>.</returns>
    public Task<bool> DeletePresetAsync(string name, CancellationToken ct) => workspace.DeletePresetAsync(name, ct);
}

/// <summary>
/// Adapts <see cref="RunManager"/> to <see cref="IRunPort"/> for the AI assistant.
/// Starting a run validates and executes the live plan held in <see cref="PlanWorkspace"/>.
/// </summary>
/// <param name="runs">The load run manager.</param>
/// <param name="workspace">The live plan workspace whose plan is executed.</param>
/// <param name="catalog">The scenario catalog used to validate the plan before starting.</param>
public sealed class RunAdapter(RunManager runs, PlanWorkspace workspace, ScenarioCatalog catalog) : IRunPort
{
    /// <summary>Gets the current run (running or last finished), or <see langword="null"/> when no run has occurred yet.</summary>
    /// <returns>The current <see cref="RunInfo"/>, or <see langword="null"/> if no run exists.</returns>
    public RunInfo? GetCurrent() => ToRunInfo(runs.GetCurrent());

    /// <summary>Validates the current live workspace plan and starts a new load run.</summary>
    /// <param name="environmentName">Optional environment name to apply to the run, or <see langword="null"/> for none.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The initial <see cref="RunInfo"/> for the started run.</returns>
    /// <exception cref="ArgumentException">Thrown when the current live plan fails validation against the scenario catalog.</exception>
    public async Task<RunInfo> StartAsync(string? environmentName, CancellationToken ct)
    {
        var plan = workspace.GetPlan();
        var problems = plan.Validate(catalog);
        if (problems.Count > 0) throw new ArgumentException("The plan cannot run yet: " + string.Join(" ", problems));
        return ToRunInfo(await runs.StartAsync(plan, environmentName, ct))!;
    }

    /// <summary>Requests cancellation of the currently active load run.</summary>
    /// <param name="ct">A token to cancel the stop request.</param>
    /// <returns>The updated <see cref="RunInfo"/> after stopping.</returns>
    public async Task<RunInfo> StopAsync(CancellationToken ct) => ToRunInfo(await runs.StopAsync())!;

    /// <summary>Maps a <see cref="LiveRunStatusDto"/> from <see cref="RunManager"/> into a <see cref="RunInfo"/>.</summary>
    /// <param name="dto">The live status DTO to map.</param>
    /// <returns>The mapped <see cref="RunInfo"/>, or <see langword="null"/> when <see cref="LiveRunStatusDto.RunId"/> is empty.</returns>
    public static RunInfo? ToRunInfo(LiveRunStatusDto dto)
    {
        if (string.IsNullOrEmpty(dto.RunId)) return null;
        var entries = dto.Plan.Scenarios.Select((c, i) =>
        {
            var v = i < dto.Scenarios.Count ? dto.Scenarios[i] : new LiveScenarioViewDto(0, 0, 0, 0, 0, 0, 0);
            return new EntryFigures(i, string.IsNullOrWhiteSpace(c.Label) ? c.ScenarioId : c.Label, v.Starts, v.Finished, v.InFlight, v.Failed, v.SlowBees, v.SlowCalls, v.Dropped, c.EffectiveRatePerMinute());
        }).ToList();
        return new RunInfo(dto.RunId, dto.State, dto.ElapsedSeconds, dto.StartedUtc, entries.Sum(e => e.Finished), entries.Sum(e => e.Failed), entries.Sum(e => e.SlowBees),
            dto.TotalLogs, dto.Error, entries, dto.PreflightState, dto.PreflightError);
    }
}
