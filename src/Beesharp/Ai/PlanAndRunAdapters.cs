using PerformanceTool.Ai;
using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.AiAdapters;

// Adapters of the assistant's ports onto the host's own services (brain 0047/0051). They are thin: mapping and a few guard rails, no logic of their own.

/// <summary><see cref="IPlanWorkspacePort"/> over <see cref="PlanWorkspace"/> (the same object the REST API and the editor use).</summary>
public sealed class PlanWorkspaceAdapter(PlanWorkspace workspace) : IPlanWorkspacePort
{
    private static PlanState Map(PlanWorkspaceState s) => new(s.Plan, s.PresetName, s.SavedPlan, s.HasUnsavedChanges, s.Version);

    public PlanState Get() => Map(workspace.GetState());
    public PlanState Update(Func<LoadPlan, bool> edit) => Map(workspace.Update(edit));
    public int AddEntry(string scenarioId) => workspace.AddEntry(scenarioId);
    public PlanState RemoveEntry(int index) => Map(workspace.RemoveEntry(index));

    public async Task<IReadOnlyList<PresetInfo>> ListPresetsAsync(CancellationToken ct) =>
        (await workspace.ListPresetsAsync(ct)).Select(p => new PresetInfo(p.Name, p.SavedAt, p.Plan.Scenarios.Count, p.Plan.DurationSeconds)).ToList();

    public Task SavePresetAsync(string name, CancellationToken ct) => workspace.SavePresetAsync(name, null, ct);
    public async Task<bool> LoadPresetAsync(string name, CancellationToken ct) => await workspace.LoadPresetAsync(name, ct) is not null;
    public Task<bool> DeletePresetAsync(string name, CancellationToken ct) => workspace.DeletePresetAsync(name, ct);
}

/// <summary><see cref="IRunPort"/> over <see cref="RunManager"/>. A run starts the workspace plan, the one the user sees in the editor.</summary>
public sealed class RunAdapter(RunManager runs, PlanWorkspace workspace, ScenarioCatalog catalog) : IRunPort
{
    public RunInfo? GetCurrent() => ToRunInfo(runs.GetCurrent());

    public async Task<RunInfo> StartAsync(string? environmentName, CancellationToken ct)
    {
        var plan = workspace.GetPlan();
        var problems = plan.Validate(catalog);
        if (problems.Count > 0) throw new ArgumentException("The plan cannot run yet: " + string.Join(" ", problems));
        return ToRunInfo(await runs.StartAsync(plan, environmentName, ct))!;
    }

    public async Task<RunInfo> StopAsync(CancellationToken ct) => ToRunInfo(await runs.StopAsync())!;

    /// <summary>Maps the live status of <see cref="RunManager"/> (null when no run exists).</summary>
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
