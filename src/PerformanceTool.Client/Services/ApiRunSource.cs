using System.Net.Http.Json;
using System.Text.Json;
using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

/// <summary>
/// Live data source talking to the ASP.NET Core server's REST API (<c>/api/runs/*</c>) so users can run real
/// built-in and custom C# script scenarios from the UI (brain 0015, 0040).
/// </summary>
public sealed class ApiRunSource : IRunSource
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly ScenarioCatalog catalog;
    private readonly ApiEnvironmentSource environments;
    private CancellationTokenSource? pollCts;
    private string savedFingerprint = "";
    private HashSet<string> savedEntries = [];
    private readonly Dictionary<string, LoadPlan> presets = new(StringComparer.OrdinalIgnoreCase);

    public LoadPlan Plan { get; private set; } = new() { DurationSeconds = 30, MaxConcurrency = 50, TimeoutSeconds = 15 };
    public string PresetName { get; private set; } = "Live plan";
    public IReadOnlyList<string> PresetNames => presets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    public bool HasUnsavedChanges => Fingerprint(Plan) != savedFingerprint;

    public RunSnapshot Current { get; private set; }
    public bool IsRunning => Current.Running;
    public event Action? Changed;

    private sealed record LiveActorDto(int Sequence, int Entry, double StartedAt, double DurationMs, int? StatusCode, long Bytes, string? Error);
    private sealed record LiveScenarioViewDto(int Starts, int InFlight, int Failed, int Dropped);
    private sealed record LiveRunStatusDto(
        string RunId,
        string State,
        double ElapsedSeconds,
        DateTimeOffset StartedUtc,
        LoadPlan Plan,
        List<LiveScenarioViewDto> Scenarios,
        List<LiveActorDto> RecentActors,
        string? Error);

    public ApiRunSource(HttpClient http, ScenarioCatalog catalog, ApiEnvironmentSource environments)
    {
        this.http = http;
        this.catalog = catalog;
        this.environments = environments;
        presets[PresetName] = Plan.Clone();
        savedFingerprint = Fingerprint(Plan);
        savedEntries = Plan.Scenarios.Select(EntryFingerprint).ToHashSet();
        Current = EmptySnapshot(Plan);
    }

    public bool IsEntryChanged(ScenarioRunConfig entry) => !savedEntries.Contains(EntryFingerprint(entry));

    public void AddEntry(IScenario scenario)
    {
        if (Plan.Scenarios.Count >= LoadPlan.MaxEntries) return;
        var config = ScenarioRunConfig.CreateFor(scenario);
        var same = Plan.Scenarios.Count(s => s.ScenarioId == scenario.Id);
        config.Label = same == 0 ? scenario.Name : $"{scenario.Name} #{same + 1}";
        config.RatePerMinute = 30;
        Plan.Scenarios.Add(config);
        if (!IsRunning) Current = EmptySnapshot(Plan);
        Changed?.Invoke();
    }

    public void RemoveEntry(int index)
    {
        if (index < 0 || index >= Plan.Scenarios.Count) return;
        Plan.Scenarios.RemoveAt(index);
        if (!IsRunning) Current = EmptySnapshot(Plan);
        Changed?.Invoke();
    }

    public void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        PresetName = name;
        presets[name] = Plan.Clone();
        savedFingerprint = Fingerprint(Plan);
        savedEntries = Plan.Scenarios.Select(EntryFingerprint).ToHashSet();
        Changed?.Invoke();
    }

    public void LoadPreset(string name)
    {
        if (!presets.TryGetValue(name, out var stored)) return;
        PresetName = name;
        Plan = stored.Clone();
        savedFingerprint = Fingerprint(Plan);
        savedEntries = Plan.Scenarios.Select(EntryFingerprint).ToHashSet();
        if (!IsRunning) Current = EmptySnapshot(Plan);
        Changed?.Invoke();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var dto = await http.GetFromJsonAsync<LiveRunStatusDto>("api/runs/current", JsonOpts);
            if (dto is not null && dto.Plan.Scenarios.Count > 0)
            {
                ApplyStatus(dto);
                if (dto.State == "Running") StartPolling();
            }
        }
        catch { }
    }

    public void Start()
    {
        if (Plan.Scenarios.Count == 0) return;
        _ = StartRemoteAsync();
    }

    public void Pause() => Stop(); // Server runner supports start/stop.
    public void Resume() => Start();

    public void Stop()
    {
        _ = StopRemoteAsync();
    }

    private async Task StartRemoteAsync()
    {
        try
        {
            var resp = await http.PostAsJsonAsync("api/runs/start", new { plan = Plan, environmentName = environments.ActiveName }, JsonOpts);
            if (resp.IsSuccessStatusCode)
            {
                var dto = await resp.Content.ReadFromJsonAsync<LiveRunStatusDto>(JsonOpts);
                if (dto is not null) ApplyStatus(dto);
                StartPolling();
            }
        }
        catch { }
    }

    private async Task StopRemoteAsync()
    {
        StopPolling();
        try
        {
            var resp = await http.PostAsync("api/runs/stop", null);
            if (resp.IsSuccessStatusCode)
            {
                var dto = await resp.Content.ReadFromJsonAsync<LiveRunStatusDto>(JsonOpts);
                if (dto is not null) ApplyStatus(dto);
            }
        }
        catch { }
    }

    private void StartPolling()
    {
        StopPolling();
        pollCts = new CancellationTokenSource();
        var ct = pollCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, ct);
                    var dto = await http.GetFromJsonAsync<LiveRunStatusDto>("api/runs/current", JsonOpts, ct);
                    if (dto is not null)
                    {
                        ApplyStatus(dto);
                        if (dto.State != "Running") break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(1000, ct); }
            }
        }, CancellationToken.None);
    }

    private void StopPolling()
    {
        pollCts?.Cancel();
        pollCts?.Dispose();
        pollCts = null;
    }

    private void ApplyStatus(LiveRunStatusDto dto)
    {
        var count = dto.Plan.Scenarios.Count;
        var store = new RunStore(count);
        var finished = new int[count];
        var slowActors = new int[count];
        var slowCalls = new int[count];

        foreach (var a in dto.RecentActors)
        {
            if (a.Entry < 0 || a.Entry >= count) continue;
            finished[a.Entry]++;
            var isSlow = a.Error is null && a.DurationMs >= 1500;
            if (isSlow) { slowActors[a.Entry]++; slowCalls[a.Entry]++; }
            var errId = store.Errors.Intern(a.Error);
            var record = new ActorRecord(a.Sequence, a.Entry, a.StartedAt, a.DurationMs, isSlow ? 1 : 0, errId, store.Errors);
            var call = new FakeCall(0, "HTTP", 0, a.DurationMs, a.StatusCode, errId, Skipped: false, ThinkBeforeMs: 0, Slow: isSlow, TypicalMs: null, Errors: store.Errors);
            var at = a.StartedAt + a.DurationMs / 1000.0;
            var ts = dto.StartedUtc.AddSeconds(at);
            var level = a.Error is not null ? "Error" : isSlow ? "Warning" : "Information";
            var logMsg = a.Error is null
                ? $"Completed in {a.DurationMs:0} ms (HTTP {a.StatusCode?.ToString() ?? "-"}, {a.Bytes} B)"
                : $"Failed in {a.DurationMs:0} ms: {a.Error}";
            var scope = new Dictionary<string, string> { ["Actor"] = record.Name };
            var detail = new ActorDetail(
                a.Sequence,
                a.Entry,
                [call],
                () => [new FakeLogEntry(at, ts, level, logMsg, errId, store.Errors, a.Entry, a.Sequence, scope, 0)]);
            store.Complete(record, detail);
        }

        var labels = dto.Plan.Scenarios.Select(s => string.IsNullOrWhiteSpace(s.Label) ? s.ScenarioId : s.Label).ToList();
        var views = new ScenarioView[count];
        for (var i = 0; i < count; i++)
        {
            var sv = i < dto.Scenarios.Count ? dto.Scenarios[i] : new LiveScenarioViewDto(0, 0, 0, 0);
            views[i] = new ScenarioView(finished[i], sv.Failed, slowActors[i], slowCalls[i], sv.InFlight, sv.Starts);
        }

        var state = dto.State switch
        {
            "Running" => FakeRunState.Running,
            "Finished" => FakeRunState.Finished,
            _ => FakeRunState.Idle,
        };

        Current = new RunSnapshot(
            state,
            dto.ElapsedSeconds,
            dto.Plan,
            labels,
            views,
            store);
        Changed?.Invoke();
    }

    private static RunSnapshot EmptySnapshot(LoadPlan plan)
    {
        var count = plan.Scenarios.Count;
        var labels = plan.Scenarios.Select(s => string.IsNullOrWhiteSpace(s.Label) ? s.ScenarioId : s.Label).ToList();
        var views = Enumerable.Range(0, count).Select(_ => new ScenarioView(0, 0, 0, 0, 0, 0)).ToArray();
        return new RunSnapshot(FakeRunState.Idle, 0, plan.Clone(), labels, views, new RunStore(count));
    }

    private static string Fingerprint(LoadPlan plan) => JsonSerializer.Serialize(plan, JsonOpts);
    private static string EntryFingerprint(ScenarioRunConfig e) => JsonSerializer.Serialize(e, JsonOpts);

    public void Dispose() => StopPolling();
}
