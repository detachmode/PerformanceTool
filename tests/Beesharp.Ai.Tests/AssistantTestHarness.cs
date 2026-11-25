using System.Text.Json.Nodes;
using Beesharp.Ai;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Ai.Tests;

/// <summary>
/// Lightweight in-memory ports for exercising <see cref="AssistantToolbox"/> stories in isolation
/// without starting the ASP.NET host or touching disk.
/// </summary>
internal sealed class InMemoryPlanPort(ScenarioCatalog catalog) : IPlanWorkspacePort
{
    private LoadPlan plan = new();
    private LoadPlan saved = new();
    private string presetName = "";
    private long version = 1;
    private readonly Dictionary<string, (LoadPlan Plan, DateTimeOffset SavedAt)> presets = new(StringComparer.OrdinalIgnoreCase);

    public PlanState Get() => new(plan.Clone(), presetName, saved.Clone(), plan.Scenarios.Count != saved.Scenarios.Count || version > 1, version);

    public PlanState Update(Func<LoadPlan, bool> edit)
    {
        var copy = plan.Clone();
        if (edit(copy))
        {
            plan = copy;
            version++;
        }
        return Get();
    }

    public int AddEntry(string scenarioId)
    {
        var scenario = catalog.Find(scenarioId) ?? throw new KeyNotFoundException($"Unknown scenario '{scenarioId}'.");
        plan.Scenarios.Add(ScenarioRunConfig.CreateFor(scenario));
        version++;
        return plan.Scenarios.Count - 1;
    }

    public PlanState RemoveEntry(int index)
    {
        plan.Scenarios.RemoveAt(index);
        version++;
        return Get();
    }

    public Task<IReadOnlyList<PresetInfo>> ListPresetsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PresetInfo>>(
            presets.Select(kv => new PresetInfo(kv.Key, kv.Value.SavedAt, kv.Value.Plan.Scenarios.Count, kv.Value.Plan.DurationSeconds)).ToList());

    public Task SavePresetAsync(string name, CancellationToken ct)
    {
        var clean = name.Trim();
        presets[clean] = (plan.Clone(), DateTimeOffset.UtcNow);
        presetName = clean;
        saved = plan.Clone();
        version = 1;
        return Task.CompletedTask;
    }

    public Task<bool> LoadPresetAsync(string name, CancellationToken ct)
    {
        if (!presets.TryGetValue(name.Trim(), out var found)) return Task.FromResult(false);
        plan = found.Plan.Clone();
        saved = found.Plan.Clone();
        presetName = name.Trim();
        version = 1;
        return Task.FromResult(true);
    }

    public Task<bool> DeletePresetAsync(string name, CancellationToken ct) =>
        Task.FromResult(presets.Remove(name.Trim()));
}

internal sealed class InMemoryRunPort : IRunPort
{
    public RunInfo? Current { get; set; }
    public string? LastStartEnvironment { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public RunInfo? GetCurrent() => Current;

    public Task<RunInfo> StartAsync(string? environmentName, CancellationToken ct)
    {
        StartCount++;
        LastStartEnvironment = environmentName;
        Current = new RunInfo(
            "run-live-1",
            "Running",
            0.5,
            DateTimeOffset.UtcNow,
            TotalBees: 0,
            FailedBees: 0,
            SlowBees: 0,
            TotalLogs: 0,
            Error: null,
            Entries: [new EntryFigures(0, "Checkout", 0, 0, 0, 0, 0, 0, 0, 60)]);
        return Task.FromResult(Current);
    }

    public Task<RunInfo> StopAsync(CancellationToken ct)
    {
        StopCount++;
        Current = (Current ?? new RunInfo("run-live-1", "Finished", 5, DateTimeOffset.UtcNow, 12, 1, 2, 30, null, []))
            with { State = "Finished", TotalBees = 12 };
        return Task.FromResult(Current);
    }
}

internal sealed class InMemoryHistoryPort : IRunHistoryPort
{
    public string? LatestId { get; set; } = "run-42";
    public List<RunInfo> Runs { get; } = [];
    public List<BeeInfo> Bees { get; } = [];
    public Dictionary<int, BeeDetailInfo> Details { get; } = [];
    public List<LogLineInfo> Logs { get; } = [];
    public List<CallEndpointInfo> Endpoints { get; } = [];
    public Dictionary<string, CallEndpointDetailInfo> EndpointDetails { get; } = new(StringComparer.OrdinalIgnoreCase);
    public LogFilter? LastLogFilter { get; private set; }
    public (int? Entry, BeeOutcomeFilter Outcome, int? FailedStep, int Offset, int Limit)? LastBeeQuery { get; private set; }

    public Task<string?> LatestRunIdAsync(CancellationToken ct) => Task.FromResult(LatestId);

    public Task<IReadOnlyList<RunInfo>> ListRunsAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunInfo>>(Runs.Take(limit).ToList());

    public Task<RunInfo?> GetRunAsync(string runId, CancellationToken ct) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.RunId == runId));

    public Task<BeePage> ListBeesAsync(string runId, int? entry, BeeOutcomeFilter outcome, int offset, int limit, CancellationToken ct, int? failedStep = null)
    {
        LastBeeQuery = (entry, outcome, failedStep, offset, limit);
        var filtered = Bees
            .Where(b => entry is null || b.Entry == entry)
            .Where(b => failedStep is null || (b.Error is not null && b.FailedStep == failedStep))
            .Where(b => outcome switch
            {
                BeeOutcomeFilter.Ok => b.Outcome == "ok",
                BeeOutcomeFilter.Slow => b.Outcome == "slow",
                BeeOutcomeFilter.Failed => b.Outcome == "failed",
                _ => true,
            })
            .ToList();
        return Task.FromResult(new BeePage(filtered.Count, filtered.Skip(offset).Take(limit).ToList()));
    }

    public Task<IReadOnlyList<BeeInfo>> GetBeesAsync(string runId, IReadOnlyList<int> sequences, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BeeInfo>>(Bees.Where(b => sequences.Contains(b.Sequence)).ToList());

    public Task<BeeDetailInfo?> GetBeeAsync(string runId, int sequence, CancellationToken ct) =>
        Task.FromResult(Details.GetValueOrDefault(sequence));

    public Task<LogPageInfo> QueryLogsAsync(string runId, LogFilter filter, CancellationToken ct)
    {
        LastLogFilter = filter;
        var items = Logs
            .Where(l => filter.Entry is null || l.Entry == filter.Entry)
            .Where(l => filter.Bee is null || l.Sequence == filter.Bee)
            .Where(l => filter.Levels is null || filter.Levels.Contains(l.Level, StringComparer.OrdinalIgnoreCase))
            .Where(l => string.IsNullOrWhiteSpace(filter.Search) || l.Message.Contains(filter.Search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(new LogPageInfo(items.Count, items.Skip(filter.Offset).Take(filter.Limit).ToList()));
    }

    public Task<CallEndpointsPage> ListCallEndpointsAsync(string runId, int? entry, bool slowOnly, bool failedOnly, double? minMs, string? search, CancellationToken ct)
    {
        var items = Endpoints.AsEnumerable();
        if (slowOnly) items = items.Where(e => e.SlowCount > 0);
        if (failedOnly) items = items.Where(e => e.FailedCount > 0);
        if (minMs is > 0) items = items.Where(e => e.MaxMs >= minMs);
        if (!string.IsNullOrWhiteSpace(search)) items = items.Where(e => e.Endpoint.Contains(search, StringComparison.OrdinalIgnoreCase));
        var list = items.ToList();
        return Task.FromResult(new CallEndpointsPage(list.Sum(e => e.TotalCount), list.Sum(e => e.SlowCount), list.Sum(e => e.FailedCount), list));
    }

    public Task<CallEndpointDetailInfo?> GetCallEndpointDetailAsync(string runId, string endpoint, int? entry, string? show, double? minMs, double? maxMs, string? sort, int offset, int limit, CancellationToken ct)
    {
        if (!EndpointDetails.TryGetValue(endpoint, out var detail)) return Task.FromResult<CallEndpointDetailInfo?>(null);
        var samples = detail.Calls.AsEnumerable();
        if (show == "slow") samples = samples.Where(s => s.IsSlow);
        else if (show == "failed") samples = samples.Where(s => s.Error is not null);
        if (minMs is > 0) samples = samples.Where(s => s.DurationMs >= minMs);
        var list = samples.ToList();
        return Task.FromResult<CallEndpointDetailInfo?>(new CallEndpointDetailInfo(detail.Summary, list.Count, offset, list.Skip(offset).Take(limit).ToList()));
    }
}

internal sealed class InMemoryEnvironmentPort : IEnvironmentPort
{
    public Dictionary<string, EnvironmentInfo> Stored { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<EnvironmentInfo>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EnvironmentInfo>>(Stored.Values.ToList());

    public Task<EnvironmentInfo?> GetAsync(string name, CancellationToken ct) =>
        Task.FromResult(Stored.GetValueOrDefault(name));

    public Task SaveAsync(EnvironmentInfo environment, CancellationToken ct)
    {
        Stored[environment.Name] = environment;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string name, CancellationToken ct) =>
        Task.FromResult(Stored.Remove(name));
}

internal sealed class InMemoryScriptPort : IScriptPort
{
    public Dictionary<string, ScriptInfo> Scripts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ScriptInfo> Preflights { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailNextScriptCompile { get; set; }
    public bool FailNextPreflightCompile { get; set; }
    public bool FailNextScriptTestRun { get; set; }
    public bool FailNextPreflightTestRun { get; set; }

    public IReadOnlyList<ScriptInfo> ListScripts() => Scripts.Values.ToList();
    public ScriptInfo? GetScript(string id) => Scripts.GetValueOrDefault(id);

    public Task<CompileOutcome> UploadScriptAsync(string? id, string? fileName, string code, CancellationToken ct)
    {
        if (FailNextScriptCompile)
        {
            FailNextScriptCompile = false;
            return Task.FromResult(new CompileOutcome(false, null, [new Diagnostic("Error", 3, 5, "CS1002: ; expected")], "Compilation failed."));
        }
        var sid = id ?? "checkout-script";
        var info = new ScriptInfo(sid, fileName ?? $"{sid}.cs", "Checkout Script", $"script-{sid}", "Custom script", DateTimeOffset.UtcNow, code);
        Scripts[sid] = info;
        return Task.FromResult(new CompileOutcome(true, info, [], null));
    }

    public Task<ScriptTestOutcome> TestRunScriptAsync(string id, JsonObject? parameters, string? environmentName, int timeoutSeconds, CancellationToken ct)
    {
        if (FailNextScriptTestRun)
        {
            FailNextScriptTestRun = false;
            return Task.FromResult(new ScriptTestOutcome(false, 500, 420, "Gateway timeout", [new TestCallInfo("POST", "https://shop.test/pay", 500, 410, "HTTP 500")], ["ERR failed"]));
        }
        return Task.FromResult(new ScriptTestOutcome(true, 200, 85, null, [new TestCallInfo("GET", "https://shop.test/items", 200, 80, null)], ["INF ok"]));
    }

    public bool DeleteScript(string id) => Scripts.Remove(id);

    public IReadOnlyList<ScriptInfo> ListPreflights() => Preflights.Values.ToList();
    public ScriptInfo? GetPreflight(string id) => Preflights.GetValueOrDefault(id);

    public Task<CompileOutcome> UploadPreflightAsync(string? id, string? fileName, string code, CancellationToken ct)
    {
        if (FailNextPreflightCompile)
        {
            FailNextPreflightCompile = false;
            return Task.FromResult(new CompileOutcome(false, null, [new Diagnostic("Error", 1, 1, "CS0103: unknown symbol")], "Compilation failed."));
        }
        var pid = id ?? "check-readiness";
        var info = new ScriptInfo(pid, fileName ?? $"{pid}.cs", "Check Readiness", null, "", DateTimeOffset.UtcNow, code);
        Preflights[pid] = info;
        return Task.FromResult(new CompileOutcome(true, info, [], null));
    }

    public Task<PreflightTestOutcome> TestRunPreflightAsync(string id, string? environmentName, int timeoutSeconds, CancellationToken ct)
    {
        if (FailNextPreflightTestRun)
        {
            FailNextPreflightTestRun = false;
            return Task.FromResult(new PreflightTestOutcome(false, 55, "Database not ready", ["ERR connection refused"]));
        }
        return Task.FromResult(new PreflightTestOutcome(true, 32, null, ["INF cluster healthy"]));
    }

    public bool DeletePreflight(string id) => Preflights.Remove(id);
}

internal static class TestCatalogFactory
{
    public sealed record StepBrowse;
    public sealed record StepPay;
    public union CheckoutStep(StepBrowse, StepPay);

    public static ScenarioCatalog CreateWithSteps()
    {
        var host = new ScenarioScriptHost();
        var stepped = host.DefineSteps<CheckoutStep>(
            "Checkout Journey",
            start: () => new StepBrowse(),
            run: (step, _) => Task.FromResult(step is StepBrowse ? (CheckoutStep?)new StepPay() : null),
            id: "script-checkout",
            description: "Two-step checkout flow.").Build();

        var catalog = new ScenarioCatalog([new HttpGetScenario(), new CustomRequestScenario()]);
        catalog.RegisterCustom(stepped);
        return catalog;
    }
}
