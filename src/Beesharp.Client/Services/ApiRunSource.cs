using System.Net.Http.Json;
using System.Text.Json;
using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

public sealed record ServerLogRowDto(
    long Id,
    int Sequence,
    string BeeName,
    int Entry,
    double AtSeconds,
    string Level,
    string Message,
    string? Exception,
    int? CallIndex,
    int BeeOutcome,
    bool IsFailed,
    bool IsSlow);

public sealed record ServerLogPageDto(
    long TotalCount,
    int Offset,
    int Limit,
    List<ServerLogRowDto> Items);

public sealed record ClientLiveLogDto(
    long Id,
    int Sequence,
    string BeeName,
    int Entry,
    double AtSeconds,
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception);

public sealed record ServerRunSummaryDto(
    string RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string State,
    double ElapsedSeconds,
    int TotalBees,
    int FailedBees,
    long TotalLogs,
    string? Error);

/// <summary>
/// Scalable live data source talking to the ASP.NET Core server's REST API (<c>/api/runs/*</c>) backed by SQLite
/// (brain 0015, 0040, 0045). Uses lightweight incremental polling (<c>afterSeq</c>) for compact bee records,
/// and lazy-loads bee calls/logs and paged/filtered log queries on demand.
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

    // The server owns the Live plan and the presets (brain 0047/0050). We keep a working copy that the editor mutates in place,
    // push it when it stopped changing, and pick up the server's version when it changed and we have no unpushed edits.
    private CancellationTokenSource? syncCts;
    private long serverVersion;
    private string syncedFingerprint = "";

    private string activeRunId = "";
    private DateTimeOffset activeStartedUtc = DateTimeOffset.UtcNow;
    private RunStore activeStore = new(0);
    private int lastSequence = -1;
    private readonly HashSet<int> loadedDetails = [];
    private readonly HashSet<int> loadingDetails = [];

    public LoadPlan Plan { get; private set; } = new() { DurationSeconds = 30, MaxConcurrency = 50, TimeoutSeconds = 15 };
    public string PresetName { get; private set; } = "Live plan";
    /// <summary>True while the working copy differs from what the server has (an edit is waiting to be pushed).</summary>
    public bool HasUnpushedEdits => syncedFingerprint.Length > 0 && Fingerprint(Plan) != syncedFingerprint;
    public IReadOnlyList<string> PresetNames => presets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    public bool HasUnsavedChanges => Fingerprint(Plan) != savedFingerprint;

    public string CurrentRunId => activeRunId;
    public DateTimeOffset CurrentStartedUtc => activeStartedUtc;
    public DateTimeOffset? ActiveStartedUtc => string.IsNullOrEmpty(activeRunId) ? null : activeStartedUtc;
    public bool IsViewingHistory { get; private set; }
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

    private sealed record LiveBeeDto(
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
        int SlowBees,
        int SlowCalls,
        int Dropped);

    private sealed record LiveRunningBeeDto(int Sequence, int Entry, double StartedAt);

    private sealed record LiveRunStatusDto(
        string RunId,
        string State,
        double ElapsedSeconds,
        DateTimeOffset StartedUtc,
        LoadPlan Plan,
        List<LiveScenarioViewDto> Scenarios,
        List<LiveBeeDto> NewBees,
        int LastSequence,
        long TotalLogs,
        string StorageProvider,
        string? Error,
        string? PreflightState = null,
        string? PreflightScriptName = null,
        List<string>? PreflightLogs = null,
        string? PreflightError = null,
        List<LiveRunningBeeDto>? RunningBees = null);

    public ApiRunSource(HttpClient http, ScenarioCatalog catalog, ApiEnvironmentSource environments)
    {
        this.http = http;
        this.catalog = catalog;
        this.environments = environments;
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
        _ = SavePresetRemoteAsync(name, Plan.Clone());
    }

    public void LoadPreset(string name)
    {
        if (presets.TryGetValue(name, out var cached))
        {
            // Instant: show the cached copy; the server's answer (normalised to today's scenario shape) replaces it.
            PresetName = presets.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            Plan = cached.Clone();
            savedFingerprint = Fingerprint(Plan);
            savedEntries = Plan.Scenarios.Select(EntryFingerprint).ToHashSet();
            if (!IsRunning && string.IsNullOrEmpty(activeRunId)) Current = EmptySnapshot(Plan);
            Changed?.Invoke();
        }
        _ = LoadPresetRemoteAsync(name);
    }

    private async Task SavePresetRemoteAsync(string name, LoadPlan plan)
    {
        try
        {
            var resp = await http.PutAsJsonAsync($"api/presets/{Uri.EscapeDataString(name)}", plan, JsonOpts);
            if (resp.IsSuccessStatusCode && await resp.Content.ReadFromJsonAsync<PlanStateDto>(JsonOpts) is { } state)
                AdoptState(state, replacePlan: false, syncedWith: plan);
            await LoadPresetsAsync();
        }
        catch { }
    }

    private async Task LoadPresetRemoteAsync(string name)
    {
        try
        {
            var resp = await http.PostAsync($"api/presets/{Uri.EscapeDataString(name)}/load", null);
            if (resp.IsSuccessStatusCode && await resp.Content.ReadFromJsonAsync<PlanStateDto>(JsonOpts) is { } state)
                AdoptState(state, replacePlan: true);
        }
        catch { }
    }

    private async Task LoadPresetsAsync()
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<PresetDto>>("api/presets", JsonOpts) ?? [];
            presets.Clear();
            foreach (var p in list) presets[p.Name] = p.Plan;
            Changed?.Invoke();
        }
        catch { }
    }

    // ---------- plan sync with the server ----------

    private sealed record PresetDto(string Name, DateTimeOffset SavedAt, LoadPlan Plan);
    private sealed record PlanStateDto(LoadPlan Plan, string PresetName, LoadPlan SavedPlan, bool HasUnsavedChanges, long Version);

    /// <summary>Starts loading and syncing the plan and presets with the server (Live mode). Safe to call again.</summary>
    public void StartSync()
    {
        if (syncCts is not null) return;
        syncCts = new CancellationTokenSource();
        var ct = syncCts.Token;
        _ = Task.Run(() => SyncLoopAsync(ct), CancellationToken.None);
    }

    public void StopSync()
    {
        syncCts?.Cancel();
        syncCts?.Dispose();
        syncCts = null;
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        string? pending = null;
        var tick = 0;
        try
        {
            // First load: the server's plan replaces the empty working copy, unless the user already started editing.
            while (!ct.IsCancellationRequested && serverVersion == 0)
            {
                try
                {
                    var state = await http.GetFromJsonAsync<PlanStateDto>("api/plan", JsonOpts, ct);
                    if (state is not null)
                    {
                        if (Plan.Scenarios.Count == 0 || Fingerprint(Plan) == savedFingerprint) AdoptState(state, replacePlan: true);
                        else { serverVersion = state.Version; syncedFingerprint = Fingerprint(state.Plan); ApplyMeta(state); }
                        await LoadPresetsAsync();
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { await Task.Delay(1500, ct); }
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(400, ct);
                try
                {
                    var fingerprint = Fingerprint(Plan);
                    if (fingerprint != syncedFingerprint)
                    {
                        // An edit: push it once it stopped changing (the editor changes it key by key).
                        if (fingerprint == pending) { await PushPlanAsync(ct); pending = null; }
                        else pending = fingerprint;
                    }
                    else if (++tick % 3 == 0)
                    {
                        pending = null;
                        await PullPlanAsync(ct);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { /* offline or restarting: try again on the next tick */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Asks the server for plan changes right now (after the assistant changed something) instead of waiting for the next tick.</summary>
    public async Task SyncPlanNowAsync()
    {
        try { await PullPlanAsync(CancellationToken.None); }
        catch { }
    }

    private async Task PushPlanAsync(CancellationToken ct)
    {
        var sent = Plan.Clone();
        var resp = await http.PutAsJsonAsync("api/plan", sent, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode) return;
        var state = await resp.Content.ReadFromJsonAsync<PlanStateDto>(JsonOpts, ct);
        if (state is null) return;
        serverVersion = state.Version;
        syncedFingerprint = Fingerprint(sent);
        ApplyMeta(state);
    }

    private async Task PullPlanAsync(CancellationToken ct)
    {
        var resp = await http.GetAsync($"api/plan?knownVersion={serverVersion}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return;
        var state = await resp.Content.ReadFromJsonAsync<PlanStateDto>(JsonOpts, ct);
        if (state is null || state.Version == serverVersion) return;
        // The user may have started editing while we waited: unpushed edits win (last writer wins, brain 0050).
        if (Fingerprint(Plan) != syncedFingerprint) return;
        AdoptState(state, replacePlan: true);
        await LoadPresetsAsync();
    }

    /// <summary>Takes the server's state as the truth. With <paramref name="replacePlan"/> false only the bookkeeping (version, preset, saved plan) is taken.</summary>
    private void AdoptState(PlanStateDto state, bool replacePlan, LoadPlan? syncedWith = null)
    {
        serverVersion = state.Version;
        if (replacePlan)
        {
            Plan = state.Plan.Clone();
            syncedFingerprint = Fingerprint(Plan);
            if (!IsRunning && string.IsNullOrEmpty(activeRunId)) Current = EmptySnapshot(Plan);
        }
        else if (syncedWith is not null) syncedFingerprint = Fingerprint(syncedWith);
        ApplyMeta(state);
    }

    private void ApplyMeta(PlanStateDto state)
    {
        PresetName = state.PresetName;
        savedFingerprint = Fingerprint(state.SavedPlan);
        savedEntries = state.SavedPlan.Scenarios.Select(EntryFingerprint).ToHashSet();
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

    public async Task<List<ClientLiveLogDto>> FetchLiveLogsAsync(long afterId = 0, int limit = 500, CancellationToken ct = default, int? sequence = null, int? entry = null)
    {
        var url = $"api/runs/live-logs?afterId={afterId}&limit={limit}";
        if (sequence is { } seq) url += $"&sequence={seq}";
        if (entry is { } ent) url += $"&entry={ent}";
        try { return await http.GetFromJsonAsync<List<ClientLiveLogDto>>(url, JsonOpts, ct) ?? []; }
        catch { return []; }
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
                if (dto is not null)
                {
                    IsViewingHistory = true;
                    ApplyStatus(dto);
                }
            }
        }
        catch { }
    }

    /// <summary>Exits history inspection mode back to the live plan view so a new run can be configured and started.</summary>
    public void ReturnToLive()
    {
        if (!IsViewingHistory) return;
        IsViewingHistory = false;
        Changed?.Invoke();
    }

    /// <summary>Lazily fetches one bee's full calls and logs from SQLite storage on demand.</summary>
    public async Task EnsureBeeDetailLoadedAsync(int sequence, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(activeRunId) || loadedDetails.Contains(sequence) || !loadingDetails.Add(sequence))
            return;
        try
        {
            var detail = await http.GetFromJsonAsync<ServerBeeDetailDto>(
                $"api/runs/{Uri.EscapeDataString(activeRunId)}/bees/{sequence}", JsonOpts, ct);
            if (detail is null) return;
            var beeDetail = RemoteBeeDetail.Build(detail, activeStore, activeStartedUtc, detail.Entry);
            activeStore.ReplaceDetail(beeDetail);
            loadedDetails.Add(sequence);
            Changed?.Invoke();
        }
        catch { }
        finally { loadingDetails.Remove(sequence); }
    }

    /// <summary>Queries server-side indexed, filtered, paged logs from SQLite storage.</summary>
    public async Task<ServerLogPageDto?> QueryLogsAsync(
        int? entry,
        int? bee,
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
        if (bee is { } a) parts.Add($"bee={a}");
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
                    await Task.Delay(PreflightState == "Running" ? 250 : 300, ct);
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

        foreach (var a in dto.NewBees)
        {
            if (a.Entry < 0 || a.Entry >= count) continue;
            var errId = activeStore.Errors.Intern(a.Error);
            var record = new BeeRecord(a.Sequence, a.Entry, a.StartedAt, a.DurationMs, a.SlowCalls, errId, activeStore.Errors);
            var placeholderCall = new FakeCall(0, "Loading…", 0, a.DurationMs, a.StatusCode, errId, Skipped: false, ThinkBeforeMs: 0, Slow: a.SlowCalls > 0, TypicalMs: null, Errors: activeStore.Errors);
            var detail = new BeeDetail(a.Sequence, a.Entry, [placeholderCall], () => []);
            activeStore.Complete(record, detail);
            if (a.Sequence > lastSequence) lastSequence = a.Sequence;
        }
        if (dto.LastSequence > lastSequence) lastSequence = dto.LastSequence;

        var labels = dto.Plan.Scenarios.Select(s => string.IsNullOrWhiteSpace(s.Label) ? s.ScenarioId : s.Label).ToList();
        var views = new ScenarioView[count];
        for (var i = 0; i < count; i++)
        {
            var sv = i < dto.Scenarios.Count ? dto.Scenarios[i] : new LiveScenarioViewDto(0, 0, 0, 0, 0, 0, 0);
            var keptCount = activeStore.Bees(i).Count;
            views[i] = new ScenarioView(keptCount, sv.Failed, sv.SlowBees, sv.SlowCalls, sv.InFlight, sv.Starts);
        }

        var state = dto.State switch
        {
            "Running" or "Preflight" => FakeRunState.Running,
            "Finished" => FakeRunState.Finished,
            _ => FakeRunState.Idle,
        };

        IReadOnlyList<RunningBee>[]? beesInFlight = null;
        if (dto.RunningBees is { Count: > 0 } && count > 0)
        {
            var buckets = new List<RunningBee>[count];
            for (var i = 0; i < count; i++) buckets[i] = [];
            foreach (var rb in dto.RunningBees)
            {
                if (rb.Entry >= 0 && rb.Entry < count)
                    buckets[rb.Entry].Add(new RunningBee(rb.Sequence, rb.Entry, rb.StartedAt, []));
            }
            beesInFlight = buckets;
        }

        Current = new RunSnapshot(
            state,
            dto.ElapsedSeconds,
            dto.Plan,
            labels,
            views,
            activeStore,
            beesInFlight);
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

    public void Dispose() { StopPolling(); StopSync(); }
}
