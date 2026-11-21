using System.Net.Http.Json;
using System.Text.Json;
using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

public sealed record ServerLogRowDto(
    long Id,
    int Sequence,
    string ActorName,
    int Entry,
    double AtSeconds,
    string Level,
    string Message,
    string? Exception,
    int? CallIndex,
    int ActorOutcome,
    bool IsFailed,
    bool IsSlow);

public sealed record ServerLogPageDto(
    long TotalCount,
    int Offset,
    int Limit,
    List<ServerLogRowDto> Items);

public sealed record ServerRunSummaryDto(
    string RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string State,
    double ElapsedSeconds,
    int TotalActors,
    int FailedActors,
    long TotalLogs,
    string? Error);

/// <summary>
/// Scalable live data source talking to the ASP.NET Core server's REST API (<c>/api/runs/*</c>) backed by SQLite
/// (brain 0015, 0040, 0045). Uses lightweight incremental polling (<c>afterSeq</c>) for compact actor records,
/// and lazy-loads actor calls/logs and paged/filtered log queries on demand.
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

    private string activeRunId = "";
    private DateTimeOffset activeStartedUtc = DateTimeOffset.UtcNow;
    private RunStore activeStore = new(0);
    private int lastSequence = -1;
    private readonly HashSet<int> loadedDetails = [];
    private readonly HashSet<int> loadingDetails = [];

    public LoadPlan Plan { get; private set; } = new() { DurationSeconds = 30, MaxConcurrency = 50, TimeoutSeconds = 15 };
    public string PresetName { get; private set; } = "Live plan";
    public IReadOnlyList<string> PresetNames => presets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    public bool HasUnsavedChanges => Fingerprint(Plan) != savedFingerprint;

    public string CurrentRunId => activeRunId;
    public DateTimeOffset CurrentStartedUtc => activeStartedUtc;
    public long TotalServerLogs { get; private set; }
    public string StorageProvider { get; private set; } = "DuckDb";
    public string? PreflightState { get; private set; }
    public string? PreflightScriptName { get; private set; }
    public IReadOnlyList<string> PreflightLogs { get; private set; } = [];
    public string? PreflightError { get; private set; }
    public bool PreflightDismissed { get; private set; }

    public void DismissPreflight()
    {
        PreflightDismissed = true;
        Changed?.Invoke();
    }

    public RunSnapshot Current { get; private set; }
    public bool IsRunning => Current.Running;
    public event Action? Changed;

    private sealed record LiveActorDto(
        int Sequence,
        int Entry,
        double StartedAt,
        double DurationMs,
        int? StatusCode,
        long Bytes,
        string? Error,
        int SlowCalls);

    private sealed record LiveScenarioViewDto(
        int Starts,
        int Finished,
        int InFlight,
        int Failed,
        int SlowActors,
        int SlowCalls,
        int Dropped);

    private sealed record LiveRunStatusDto(
        string RunId,
        string State,
        double ElapsedSeconds,
        DateTimeOffset StartedUtc,
        LoadPlan Plan,
        List<LiveScenarioViewDto> Scenarios,
        List<LiveActorDto> NewActors,
        int LastSequence,
        long TotalLogs,
        string StorageProvider,
        string? Error,
        string? PreflightState = null,
        string? PreflightScriptName = null,
        List<string>? PreflightLogs = null,
        string? PreflightError = null);

    private sealed record ServerActorDetailDto(
        string RunId,
        int Sequence,
        int Entry,
        double StartedAt,
        double DurationMs,
        int? StatusCode,
        long Bytes,
        string? Error,
        List<RecordedCall> Calls,
        List<RecordedLog> Logs);

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
            var dto = await http.GetFromJsonAsync<LiveRunStatusDto>($"api/runs/current?afterSeq={lastSequence}", JsonOpts);
            if (dto is not null && dto.Plan.Scenarios.Count > 0)
            {
                ApplyStatus(dto);
                if (dto.State is "Running" or "Preflight") StartPolling();
            }
        }
        catch { }
    }

    public async Task<List<ServerRunSummaryDto>> ListRunsAsync(int limit = 30, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<ServerRunSummaryDto>>($"api/runs?limit={limit}", JsonOpts, ct) ?? []; }
        catch { return []; }
    }

    public async Task LoadStoredRunAsync(string targetRunId, CancellationToken ct = default)
    {
        try
        {
            StopPolling();
            var resp = await http.PostAsync($"api/runs/{Uri.EscapeDataString(targetRunId)}/load", null, ct);
            if (resp.IsSuccessStatusCode)
            {
                var dto = await resp.Content.ReadFromJsonAsync<LiveRunStatusDto>(JsonOpts, ct);
                if (dto is not null) ApplyStatus(dto);
            }
        }
        catch { }
    }

    /// <summary>Lazily fetches one actor's full calls and logs from SQLite storage on demand.</summary>
    public async Task EnsureActorDetailLoadedAsync(int sequence, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(activeRunId) || loadedDetails.Contains(sequence) || !loadingDetails.Add(sequence))
            return;
        try
        {
            var detail = await http.GetFromJsonAsync<ServerActorDetailDto>(
                $"api/runs/{Uri.EscapeDataString(activeRunId)}/actors/{sequence}", JsonOpts, ct);
            if (detail is null) return;

            var calls = new List<FakeCall>(detail.Calls.Count);
            for (var i = 0; i < detail.Calls.Count; i++)
            {
                var c = detail.Calls[i];
                var slow = c.Error is null && c.DurationMs >= 1500;
                calls.Add(new FakeCall(i, c.Name, c.OffsetMs, c.DurationMs, c.StatusCode,
                    activeStore.Errors.Intern(c.Error), Skipped: false, ThinkBeforeMs: 0, Slow: slow, TypicalMs: null, Errors: activeStore.Errors));
            }
            if (calls.Count == 0)
                calls.Add(new FakeCall(0, "Run", 0, detail.DurationMs, detail.StatusCode,
                    activeStore.Errors.Intern(detail.Error), Skipped: false, ThinkBeforeMs: 0, Slow: detail.Error is null && detail.DurationMs >= 1500, TypicalMs: null, Errors: activeStore.Errors));

            var scope = new Dictionary<string, string> { ["Actor"] = $"Actor #{sequence + 1}" };
            var startedUtc = activeStartedUtc;
            var actorLogs = detail.Logs;
            var actorDetail = new ActorDetail(
                sequence,
                detail.Entry,
                calls,
                () => actorLogs.Select(l => new FakeLogEntry(
                    l.AtSeconds,
                    startedUtc.AddSeconds(l.AtSeconds),
                    l.Level,
                    l.Message,
                    activeStore.Errors.Intern(l.Exception),
                    activeStore.Errors,
                    detail.Entry,
                    sequence,
                    scope,
                    l.CallIndex)).ToList());

            activeStore.ReplaceDetail(actorDetail);
            loadedDetails.Add(sequence);
            Changed?.Invoke();
        }
        catch { }
        finally { loadingDetails.Remove(sequence); }
    }

    /// <summary>Queries server-side indexed, filtered, paged logs from SQLite storage.</summary>
    public async Task<ServerLogPageDto?> QueryLogsAsync(
        int? entry,
        int? actor,
        bool? failed,
        bool? slow,
        IReadOnlyList<string>? levels,
        string? search,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(activeRunId)) return new ServerLogPageDto(0, offset, limit, []);
        var parts = new List<string> { $"offset={offset}", $"limit={limit}" };
        if (entry is { } e) parts.Add($"entry={e}");
        if (actor is { } a) parts.Add($"actor={a}");
        if (failed is { } fl) parts.Add($"failed={(fl ? "true" : "false")}");
        if (slow is { } sl) parts.Add($"slow={(sl ? "true" : "false")}");
        if (levels is { Count: > 0 } lv && lv.Count < 5) parts.Add($"levels={Uri.EscapeDataString(string.Join(',', lv))}");
        if (!string.IsNullOrWhiteSpace(search)) parts.Add($"search={Uri.EscapeDataString(search.Trim())}");

        var url = $"api/runs/{Uri.EscapeDataString(activeRunId)}/logs?{string.Join('&', parts)}";
        return await http.GetFromJsonAsync<ServerLogPageDto>(url, JsonOpts, ct);
    }

    public void Start()
    {
        if (Plan.Scenarios.Count == 0) return;
        _ = StartRemoteAsync();
    }

    public void Pause() => Stop();
    public void Resume() => Start();

    public void Stop() => _ = StopRemoteAsync();

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
                    await Task.Delay(PreflightState == "Running" ? 250 : 500, ct);
                    var dto = await http.GetFromJsonAsync<LiveRunStatusDto>($"api/runs/current?afterSeq={lastSequence}", JsonOpts, ct);
                    if (dto is not null)
                    {
                        ApplyStatus(dto);
                        if (dto.State is not ("Running" or "Preflight")) break;
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
        if (Plan.Scenarios.Count == 0 && count > 0)
        {
            Plan = dto.Plan.Clone();
            savedFingerprint = Fingerprint(Plan);
            savedEntries = Plan.Scenarios.Select(EntryFingerprint).ToHashSet();
        }
        if (!string.Equals(activeRunId, dto.RunId, StringComparison.Ordinal))
        {
            activeRunId = dto.RunId;
            activeStartedUtc = dto.StartedUtc;
            activeStore = new RunStore(count);
            lastSequence = -1;
            loadedDetails.Clear();
            loadingDetails.Clear();
            PreflightDismissed = false;
        }

        TotalServerLogs = dto.TotalLogs;
        StorageProvider = dto.StorageProvider;
        if (dto.PreflightState == "Running") PreflightDismissed = false;
        PreflightState = dto.PreflightState;
        PreflightScriptName = dto.PreflightScriptName;
        PreflightLogs = dto.PreflightLogs ?? [];
        PreflightError = dto.PreflightError;

        foreach (var a in dto.NewActors)
        {
            if (a.Entry < 0 || a.Entry >= count) continue;
            var errId = activeStore.Errors.Intern(a.Error);
            var record = new ActorRecord(a.Sequence, a.Entry, a.StartedAt, a.DurationMs, a.SlowCalls, errId, activeStore.Errors);
            var placeholderCall = new FakeCall(0, "Loading…", 0, a.DurationMs, a.StatusCode, errId, Skipped: false, ThinkBeforeMs: 0, Slow: a.SlowCalls > 0, TypicalMs: null, Errors: activeStore.Errors);
            var detail = new ActorDetail(a.Sequence, a.Entry, [placeholderCall], () => []);
            activeStore.Complete(record, detail);
            if (a.Sequence > lastSequence) lastSequence = a.Sequence;
        }
        if (dto.LastSequence > lastSequence) lastSequence = dto.LastSequence;

        var labels = dto.Plan.Scenarios.Select(s => string.IsNullOrWhiteSpace(s.Label) ? s.ScenarioId : s.Label).ToList();
        var views = new ScenarioView[count];
        for (var i = 0; i < count; i++)
        {
            var sv = i < dto.Scenarios.Count ? dto.Scenarios[i] : new LiveScenarioViewDto(0, 0, 0, 0, 0, 0, 0);
            var keptCount = activeStore.Actors(i).Count;
            views[i] = new ScenarioView(keptCount, sv.Failed, sv.SlowActors, sv.SlowCalls, sv.InFlight, sv.Starts);
        }

        var state = dto.State switch
        {
            "Running" or "Preflight" => FakeRunState.Running,
            "Finished" => FakeRunState.Finished,
            _ => FakeRunState.Idle,
        };

        Current = new RunSnapshot(
            state,
            dto.ElapsedSeconds,
            dto.Plan,
            labels,
            views,
            activeStore);
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
