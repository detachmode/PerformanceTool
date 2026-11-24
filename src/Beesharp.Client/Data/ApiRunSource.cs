using System.Net.Http.Json;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;
using Beesharp.Client.Logs;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Data;

/// <summary>
/// Scalable live data source talking to the ASP.NET Core server's REST API (<c>/api/runs/*</c>) backed by the run storage (DuckDB)
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
    public bool IsServerReachable { get; private set; } = true;
    public long TotalServerLogs { get; private set; }

    private void SetReachable(bool ok)
    {
        if (IsServerReachable == ok) return;
        IsServerReachable = ok;
        Changed?.Invoke();
    }
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

    // Runtime data (brain 0052): fetched from /api/runs/{id}/runtime whenever the status says the version changed.
    private RuntimeDataSnapshot? runtime;
    private long runtimeVersion = -1;
    private bool runtimeFetching;

    private static bool IsActiveState(string state) => state is "Running" or "Preflight" or "Shutdown";

    public RunSnapshot Current { get; private set; }
    public bool IsRunning => Current.Running;
    public event Action? Changed;

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
            if (resp.IsSuccessStatusCode && await resp.Content.ReadFromJsonAsync<PlanWorkspaceState>(JsonOpts) is { } state)
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
            if (resp.IsSuccessStatusCode && await resp.Content.ReadFromJsonAsync<PlanWorkspaceState>(JsonOpts) is { } state)
                AdoptState(state, replacePlan: true);
        }
        catch { }
    }

    private async Task LoadPresetsAsync()
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<Preset>>("api/presets", JsonOpts) ?? [];
            presets.Clear();
            foreach (var p in list) presets[p.Name] = p.Plan;
            Changed?.Invoke();
        }
        catch { }
    }

    // ---------- plan sync with the server ----------

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
                    var state = await http.GetFromJsonAsync<PlanWorkspaceState>("api/plan", JsonOpts, ct);
                    SetReachable(true);
                    if (state is not null)
                    {
                        if (Plan.Scenarios.Count == 0 || Fingerprint(Plan) == savedFingerprint) AdoptState(state, replacePlan: true);
                        else { serverVersion = state.Version; syncedFingerprint = Fingerprint(state.Plan); ApplyMeta(state); }
                        await LoadPresetsAsync();
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { SetReachable(false); await Task.Delay(1500, ct); }
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
                catch { SetReachable(false); /* offline or restarting: try again on the next tick */ }
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
        SetReachable(true);
        if (!resp.IsSuccessStatusCode) return;
        var state = await resp.Content.ReadFromJsonAsync<PlanWorkspaceState>(JsonOpts, ct);
        if (state is null) return;
        serverVersion = state.Version;
        syncedFingerprint = Fingerprint(sent);
        ApplyMeta(state);
    }

    private async Task PullPlanAsync(CancellationToken ct)
    {
        var resp = await http.GetAsync($"api/plan?knownVersion={serverVersion}", ct);
        SetReachable(true);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return;
        var state = await resp.Content.ReadFromJsonAsync<PlanWorkspaceState>(JsonOpts, ct);
        if (state is null || state.Version == serverVersion) return;
        // The user may have started editing while we waited: unpushed edits win (last writer wins, brain 0050).
        if (Fingerprint(Plan) != syncedFingerprint) return;
        AdoptState(state, replacePlan: true);
        await LoadPresetsAsync();
    }

    /// <summary>Takes the server's state as the truth. With <paramref name="replacePlan"/> false only the bookkeeping (version, preset, saved plan) is taken.</summary>
    private void AdoptState(PlanWorkspaceState state, bool replacePlan, LoadPlan? syncedWith = null)
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

    private void ApplyMeta(PlanWorkspaceState state)
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
            SetReachable(true);
            if (dto is not null && dto.Plan.Scenarios.Count > 0)
            {
                ApplyStatus(dto);
                if (IsActiveState(dto.State)) StartPolling();
            }
        }
        catch { SetReachable(false); }
    }

    public async Task<List<LiveLogEntryDto>> FetchLiveLogsAsync(long afterId = 0, int limit = 500, CancellationToken ct = default, int? sequence = null, int? entry = null)
    {
        var url = $"api/runs/live-logs?afterId={afterId}&limit={limit}";
        if (sequence is { } seq) url += $"&sequence={seq}";
        if (entry is { } ent) url += $"&entry={ent}";
        try { return await http.GetFromJsonAsync<List<LiveLogEntryDto>>(url, JsonOpts, ct) ?? []; }
        catch { return []; }
    }

    public async Task<List<StoredRunSummary>> ListRunsAsync(int limit = 30, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<StoredRunSummary>>($"api/runs?limit={limit}", JsonOpts, ct) ?? []; }
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

    /// <summary>Lazily fetches one bee's full calls and logs from run storage on demand.</summary>
    public async Task EnsureBeeDetailLoadedAsync(int sequence, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(activeRunId) || loadedDetails.Contains(sequence) || !loadingDetails.Add(sequence))
            return;
        try
        {
            var detail = await http.GetFromJsonAsync<StoredBeeDetail>(
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

    /// <summary>Queries the server's indexed, filtered, paged logs of the loaded run (a live run or one from the history).</summary>
    public async Task<LogPageResult?> QueryLogsAsync(LogFilter filter, int offset, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(activeRunId) || filter.Contradicts) return new LogPageResult(0, offset, limit, []);
        var url = $"api/runs/{Uri.EscapeDataString(activeRunId)}/logs?offset={offset}&limit={limit}{LogFilterParts(filter)}";
        return await http.GetFromJsonAsync<LogPageResult>(url, JsonOpts, ct);
    }

    /// <summary>The counts behind the Logs page's filter column for the loaded run (brain 0057).</summary>
    public async Task<LogFacets?> QueryLogFacetsAsync(LogFilter filter, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(activeRunId)) return new LogFacets(0, new Dictionary<string, long>(), [], []);
        if (filter.Contradicts) return new LogFacets(0, new Dictionary<string, long>(), [], []);
        var parts = LogFilterParts(filter);
        var url = $"api/runs/{Uri.EscapeDataString(activeRunId)}/logs/facets{(parts.Length > 0 ? "?" + parts[1..] : "")}";
        return await http.GetFromJsonAsync<LogFacets>(url, JsonOpts, ct);
    }

    /// <summary>"&amp;entry=1&amp;level..." parts of the server's log query for a filter.</summary>
    private static string LogFilterParts(LogFilter f)
    {
        var sb = new System.Text.StringBuilder();
        if (f.Entry is { } e) sb.Append("&entry=").Append(e);
        if (f.Step is { } st && f.Entry is not null) sb.Append("&step=").Append(st);
        if (f.EffectiveBee is { } b) sb.Append("&bee=").Append(b);
        if (f.Outcome == Beesharp.Client.Chart.BeeShow.Failed) sb.Append("&failed=true");
        if (f.Outcome == Beesharp.Client.Chart.BeeShow.Slow) sb.Append("&slow=true");
        if (f.Levels.Count > 0) sb.Append("&levels=").Append(Uri.EscapeDataString(string.Join(',', f.Levels)));
        if (f.SearchParts.Text.Length > 0) sb.Append("&search=").Append(Uri.EscapeDataString(f.SearchParts.Text));
        return sb.ToString();
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
                if (dto is not null)
                {
                    ApplyStatus(dto);
                    // The shutdown program still runs after a stop (brain 0052): keep watching until it is done.
                    if (IsActiveState(dto.State)) StartPolling();
                }
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
                        if (!IsActiveState(dto.State)) break;
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

    private readonly HashSet<int> seenSequences = [];

    private void ApplyStatus(LiveRunStatusDto dto)
    {
        var count = dto.Plan.Scenarios.Count;
        if (!string.Equals(activeRunId, dto.RunId, StringComparison.Ordinal))
        {
            activeRunId = dto.RunId;
            activeStartedUtc = dto.StartedUtc;
            activeStore = new RunStore(count);
            lastSequence = -1;
            seenSequences.Clear();
            loadedDetails.Clear();
            loadingDetails.Clear();
            PreflightDismissed = false;
            runtime = null;
            runtimeVersion = -1;
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
            if (!seenSequences.Add(a.Sequence)) continue; // two overlapping polls can deliver the same bee twice
            var errId = activeStore.Errors.Intern(a.Error);
            var record = new BeeRecord(a.Sequence, a.Entry, a.StartedAt, a.DurationMs, a.SlowCalls, errId, activeStore.Errors, FailedStep: a.FailedStep, FailedRep: a.FailedRep);
            var placeholderCall = new FakeCall(0, "Loading…", 0, a.DurationMs, a.StatusCode, errId, Skipped: false, ThinkBeforeMs: 0, Slow: a.SlowCalls > 0, TypicalMs: null, Errors: activeStore.Errors, Step: Math.Max(0, a.FailedStep), Rep: Math.Max(0, a.FailedRep));
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
            "Running" or "Preflight" or "Shutdown" => FakeRunState.Running,
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
                    buckets[rb.Entry].Add(new RunningBee(rb.Sequence, rb.Entry, rb.StartedAt, [], rb.Step, rb.Rep, rb.StepVisits));
            }
            beesInFlight = buckets;
        }

        IReadOnlyList<ScenarioSteps>? steps = null;
        if (count > 0)
        {
            var mapped = new ScenarioSteps[count];
            var anySteps = false;
            for (var i = 0; i < count; i++)
            {
                var raw = dto.Steps is not null && i < dto.Steps.Count ? dto.Steps[i] : catalog.Find(dto.Plan.Scenarios[i].ScenarioId)?.Steps;
                if (raw is { Count: > 0 })
                {
                    anySteps = true;
                    var hasTimes = raw.Any(st => st.Times is > 1);
                    mapped[i] = new ScenarioSteps(
                        raw.Select(st => st.Name).ToArray(),
                        hasTimes ? raw.Select(st => st.Times ?? 1).ToArray() : null);
                }
                else
                {
                    mapped[i] = ScenarioSteps.None;
                }
            }
            if (anySteps) steps = mapped;
        }

        Current = new RunSnapshot(
            state,
            dto.ElapsedSeconds,
            dto.Plan,
            labels,
            views,
            activeStore,
            beesInFlight,
            runtime,
            dto.ShutdownState is null ? null : new ProgramRun(dto.ShutdownState, dto.ShutdownScriptName ?? "shutdown.cs", dto.ShutdownError, dto.ShutdownLogs ?? []),
            dto.PreflightState is null ? null : new ProgramRun(dto.PreflightState, dto.PreflightScriptName ?? "preflight.cs", dto.PreflightError, dto.PreflightLogs ?? []),
            steps);
        Changed?.Invoke();
        if (dto.RunId.Length > 0 && dto.RuntimeVersion != runtimeVersion && !runtimeFetching) _ = FetchRuntimeAsync(dto.RunId, dto.RuntimeVersion, count);
    }

    /// <summary>Loads the runtime data of a run (live values, or the final values of a past run) and puts it into <see cref="Current"/>.</summary>
    private async Task FetchRuntimeAsync(string runId, long version, int entries)
    {
        runtimeFetching = true;
        try
        {
            var resp = await http.GetAsync($"api/runs/{Uri.EscapeDataString(runId)}/runtime");
            RuntimeDataSnapshot? loaded;
            if (resp.IsSuccessStatusCode) loaded = await resp.Content.ReadFromJsonAsync<RuntimeDataSnapshot>(JsonOpts);
            else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) loaded = RuntimeDataSnapshot.Empty(entries); // an older run that kept none
            else return;
            if (loaded is null || !string.Equals(activeRunId, runId, StringComparison.Ordinal)) return;
            runtime = loaded;
            runtimeVersion = version;
            Current = Current with { Runtime = loaded };
            Changed?.Invoke();
        }
        catch { /* offline: the next poll tries again */ }
        finally { runtimeFetching = false; }
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
