using Beesharp.Services;
using System.Text;
using System.Text.Json.Nodes;
using Beesharp.Services.Scenarios;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Data;

// Fake data source (brain 0015): the in-browser simulator behind IRunSource. Used by the GitHub Pages demo and while the real API does not exist yet.
// It is built like the real thing has to be (brain 0021): counters are incremental, finished bees are tiny records, and calls and log
// lines are only kept for the latest bees of each scenario. WebAssembly is single threaded, so there is no locking here.

/// <summary>One call of a story: its name and typical duration. Repeated calls (e.g. five product pages) get their own entry.</summary>
/// <param name="Pause">Whether the user pauses (think time) before this call; false for calls a page fires together.</param>
/// <param name="Step">Index into the profile's declared <see cref="FakeProfile.Steps"/> (brain 0056); 0 when the scenario has no steps.</param>
public sealed record CallSpec(string Name, double MedianMs, double Sigma = 0.15, bool Pause = true, int Step = 0, int Rep = 0, double? SlowThresholdMs = null);

/// <summary>
/// How a fake scenario (a whole user story, docs/context.md) behaves: its chained calls with their typical durations, think time
/// between them, failure rate and failure kinds. The bee's duration is the sum of its calls and the think times.
/// </summary>
public sealed record FakeProfile(
    string? Description,
    IReadOnlyList<CallSpec> Calls,
    double ThinkMs = 0,
    double TailChance = 0,
    double ErrorRate = 0,
    IReadOnlyList<FakeError>? Errors = null,
    int MinCalls = 0,
    int? FailAtCall = null,
    int? ForceFailOn = null,
    double CallTimeoutMs = 10_000,
    bool RandomFail = false,
    IReadOnlyList<string>? Steps = null,
    IReadOnlyList<int>? StepTimes = null)
{
    /// <summary>The scenario's declared steps (brain 0056), known before the run; none for a scenario without steps.</summary>
    public ScenarioSteps DeclaredSteps => Steps is { Count: > 0 } ? new ScenarioSteps(Steps, StepTimes) : ScenarioSteps.None;

    public static FakeProfile Generic => new(null, [new("GET /", 150)], 0, 0.02, 0.01,
        [new(500, "HTTP 500 Internal Server Error", 1)]);
}

/// <summary>A failure kind. <see cref="Status"/> null means no response (timeout).</summary>
/// <param name="AtCall">The call (index in the story) this failure always happens at, so failures cluster at a step and repetition; null = the profile's rule.</param>
public sealed record FakeError(int? Status, string Message, double Weight, int? AtCall = null);

public sealed record FakePreset(string Name, LoadPlan Plan, List<FakeProfile> Profiles);

/// <summary>
/// Simulates a run of a few scenarios with different rates, realistic call durations and some failures, so the UI can be
/// designed without the real runner. Bees are in flight for their duration and write fake log statements that line up with
/// their outcome. It starts out with a finished run so screenshots are never empty; <c>simulateSeconds</c> (URL <c>?simulate=3h</c>; <c>burstBees</c> is the debug URL <c>?burst=100</c>)
/// pre-generates a much longer one to test scaling.
/// </summary>
public sealed class FakeRunSource : IRunSource
{
    private readonly ScenarioCatalog catalog;
    private readonly Random rng = new(7);
    private CancellationTokenSource? cts;
    private string savedFingerprint = "";
    private HashSet<string> savedEntries = [];

    // The editable plan (what the editor page changes) and the profile of each entry.
    public LoadPlan Plan { get; private set; } = new();
    public List<FakeProfile> Profiles { get; private set; } = [];
    public string PresetName { get; private set; } = "Black Friday mix";
    public List<FakePreset> Presets { get; } = [];
    public IReadOnlyList<string> PresetNames => Presets.Select(p => p.Name).ToList();
    public bool HasUnsavedChanges => Fingerprint(Plan) != savedFingerprint;

    // Run state.
    private LoadPlan runPlan = new();
    private List<FakeProfile> runProfiles = [];
    private FakeRunState state = FakeRunState.Idle;
    private double elapsed;
    private RunStore store = new(0);
    private CallTypicals typicals = new();
    private RuntimeData runtime = new(0);
    private ProgramRun? shutdown;
    private ProgramRun? preflight;
    private FakeRuntimeScript script = new(new RuntimeData(0));
    private int[] starts = [], inFlight = [], failed = [], slowBees = [], slowCalls = [];
    private double[] nextFire = [];
    private readonly List<Pending> pending = [];
    private int sequence;
    private string runId = "";
    private DateTimeOffset runStartedUtc;
    private System.Diagnostics.Stopwatch sw = new();
    private double pausedTotal, pauseAt;

    private sealed record Pending(int Entry, int Sequence, double StartedAt, double EndAt, double ElapsedMs, List<FakeCall> Calls, string? Error, FakeProfile Profile);

    public RunSnapshot Current { get; private set; } = default!;
    public bool IsRunning => state is FakeRunState.Running or FakeRunState.Paused;

    /// <summary>Raised whenever <see cref="Current"/> changes. Pages must marshal with InvokeAsync.</summary>
    public event Action? Changed;

    /// <summary>Limits of the next run's <see cref="RunStore"/> (brain 0028). Changing a value also applies to the run in progress.</summary>
    public RunStoreOptions StoreOptions { get; }

    /// <summary>Length of the finished run that is there on first load (brain 0039).</summary>
    public const int SeedRunSeconds = 30 * 60;

    public FakeRunSource(ScenarioCatalog catalog, double simulateSeconds = 0, RunStoreOptions? storeOptions = null, int burstBees = 0, IReadOnlyList<int>? stepCounts = null)
    {
        this.catalog = catalog;
        StoreOptions = storeOptions ?? new RunStoreOptions();
        LoadBlackFriday();
        // Debug (URL "?burst=100", brain 0043): every burst entry starts that many bees at once, a few of them fail, to test big bursts.
        if (burstBees > 0)
        {
            Plan.MaxConcurrency = Math.Max(Plan.MaxConcurrency, burstBees * 3);
            for (var i = 0; i < Plan.Scenarios.Count; i++)
                if (Plan.Scenarios[i].Pattern == SpawningPattern.Bursts)
                {
                    Plan.Scenarios[i].BeesPerBurst = burstBees;
                    Profiles[i] = Profiles[i] with { ErrorRate = 0.02 };
                }
        }
        // Debug (URL "?stepcount=1,8,24", brain 0056): one more entry per number, with that many steps (some with long names), to test the steps UI at any size.
        foreach (var n in stepCounts ?? [])
            AddStepsTest(n);
        Presets.Add(new("Black Friday mix", Plan.Clone(), [.. Profiles]));
        Presets.Add(MakeSmoke());
        savedFingerprint = Fingerprint(Plan);
        savedEntries = Plan.Scenarios.Select(EntryFingerprint).ToHashSet();
        // Pre-seed a finished run, simulated in virtual time.
        BeginRun();
        // The seeded run is 30 minutes (brain 0039) so there is plenty to zoom into; "?simulate=" overrides. The editable plan keeps its own duration.
        runPlan.DurationSeconds = simulateSeconds > 0 ? (int)simulateSeconds : SeedRunSeconds;
        runStartedUtc = DateTimeOffset.UtcNow.AddSeconds(-runPlan.DurationSeconds - 20);
        var step = 0.25;
        for (var t = step; t < runPlan.DurationSeconds + 60; t += step) { Step(t); if (state != FakeRunState.Running) break; }
        if (state == FakeRunState.Running) Finish(runPlan.DurationSeconds);
        Current = Snapshot();
    }

    // ---------- editing the plan (in memory) ----------

    public void AddEntry(IScenario scenario)
    {
        var config = ScenarioRunConfig.CreateFor(scenario);
        var same = Plan.Scenarios.Count(s => s.ScenarioId == scenario.Id);
        if (same > 0) config.Label += $" #{same + 1}";
        Plan.Scenarios.Add(config);
        Profiles.Add(FakeProfile.Generic);
    }

    public void RemoveEntry(int index)
    {
        if (index < 0 || index >= Plan.Scenarios.Count) return;
        Plan.Scenarios.RemoveAt(index);
        Profiles.RemoveAt(index);
    }

    public void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Presets.Add(new(name, Plan.Clone(), [.. Profiles]));
        PresetName = name;
        savedFingerprint = Fingerprint(Plan);
        savedEntries = Plan.Scenarios.Select(EntryFingerprint).ToHashSet();
        Changed?.Invoke();
    }

    public void LoadPreset(string name)
    {
        if (Presets.FirstOrDefault(p => p.Name == name) is not { } p) return;
        Plan = p.Plan.Clone();
        Profiles = [.. p.Profiles];
        PresetName = p.Name;
        savedFingerprint = Fingerprint(Plan);
        savedEntries = Plan.Scenarios.Select(EntryFingerprint).ToHashSet();
        Changed?.Invoke();
    }

    public bool IsEntryChanged(ScenarioRunConfig entry) => !savedEntries.Contains(EntryFingerprint(entry));

    private static string EntryFingerprint(ScenarioRunConfig s) => $"{s.ScenarioId}|{s.Label}|{s.RatePerMinute}|{s.Pattern}|{s.BeesPerBurst}|{s.BurstsPerMinute}|{s.MaxInFlight}|{s.Parameters.ToJsonString()}";

    /// <summary>A string that changes whenever anything the editor can change changes.</summary>
    private static string Fingerprint(LoadPlan plan)
    {
        var sb = new StringBuilder().Append(plan.DurationSeconds).Append('|').Append(plan.MaxConcurrency).Append('|').Append(plan.TimeoutSeconds);
        foreach (var s in plan.Scenarios)
            sb.Append('\n').Append(s.ScenarioId).Append('|').Append(s.Label).Append('|').Append(s.RatePerMinute).Append('|').Append(s.Pattern).Append('|').Append(s.BeesPerBurst).Append('|').Append(s.BurstsPerMinute).Append('|').Append(s.MaxInFlight).Append('|').Append(s.Parameters.ToJsonString());
        return sb.ToString();
    }

    // ---------- run control ----------

    public void Start()
    {
        if (IsRunning) return;
        BeginRun();
        Current = Snapshot();
        Changed?.Invoke();
        cts = new CancellationTokenSource();
        _ = LoopAsync(cts.Token);
    }

    /// <summary>Pause (brain 0027): no new bees start and the run clock stands still; bees already running finish.</summary>
    public void Pause()
    {
        if (state != FakeRunState.Running) return;
        state = FakeRunState.Paused;
        pauseAt = sw.Elapsed.TotalSeconds;
        Current = Snapshot();
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (state != FakeRunState.Paused) return;
        pausedTotal += sw.Elapsed.TotalSeconds - pauseAt;
        state = FakeRunState.Running;
        Current = Snapshot();
        Changed?.Invoke();
    }

    public void Stop()
    {
        cts?.Cancel();
        if (IsRunning) Finish(Math.Min(elapsed, runPlan.DurationSeconds));
        Current = Snapshot();
        Changed?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var clock = sw = System.Diagnostics.Stopwatch.StartNew();
        pausedTotal = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (state == FakeRunState.Paused) CompletePending(elapsed + clock.Elapsed.TotalSeconds - pauseAt);
                else Step(clock.Elapsed.TotalSeconds - pausedTotal);
                var done = state == FakeRunState.Finished;
                if (++tick % 3 == 0 || done)
                {
                    Current = Snapshot();
                    Changed?.Invoke();
                }
                if (done) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void BeginRun()
    {
        runPlan = Plan.Clone();
        runProfiles = [.. Profiles];
        var n = runPlan.Scenarios.Count;
        pending.Clear();
        store = new RunStore(n, StoreOptions);
        typicals = new CallTypicals();
        runtime = new RuntimeData(n);
        script = new FakeRuntimeScript(runtime);
        script.Begin(n);
        shutdown = null;
        preflight = script.RunPreflight("check-readiness.cs");
        starts = new int[n]; inFlight = new int[n]; failed = new int[n]; slowBees = new int[n]; slowCalls = new int[n];
        // First fire at a random offset inside the first interval, like independent arrival streams.
        nextFire = runPlan.Scenarios.Select(s => rng.NextDouble() * FireInterval(s)).ToArray();
        sequence = 0;
        elapsed = 0;
        runId = "run-" + rng.Next(0x1000000).ToString("x6");
        runStartedUtc = DateTimeOffset.UtcNow;
        state = FakeRunState.Running;
    }

    /// <summary>Advances the simulation to <paramref name="now"/> seconds: fires due bees, completes finished ones.</summary>
    private void Step(double now)
    {
        if (state != FakeRunState.Running) return;
        elapsed = Math.Min(now, runPlan.DurationSeconds);
        for (var i = 0; i < runPlan.Scenarios.Count; i++)
        {
            var interval = FireInterval(runPlan.Scenarios[i]);
            var perTick = runPlan.Scenarios[i].Pattern == SpawningPattern.Bursts ? Math.Max(1, runPlan.Scenarios[i].BeesPerBurst) : 1;
            while (nextFire[i] <= elapsed && nextFire[i] < runPlan.DurationSeconds)
            {
                // A burst starts all its bees at the same moment (brain 0034).
                for (var k = 0; k < perTick; k++) Fire(i, nextFire[i]);
                nextFire[i] += interval * (0.9 + rng.NextDouble() * 0.2);
            }
        }
        CompletePending(now);
        if (now >= runPlan.DurationSeconds && pending.Count == 0) Finish(runPlan.DurationSeconds);
    }

    /// <summary>Seconds between two fire moments: between bees (linear) or between bursts.</summary>
    private static double FireInterval(ScenarioRunConfig s) =>
        60 / Math.Max(0.1, s.Pattern == SpawningPattern.Bursts ? s.BurstsPerMinute : s.RatePerMinute);

    /// <summary>Completes the bees whose end has come. While paused the time keeps running for them only (they finish, nothing new starts).</summary>
    private void CompletePending(double now)
    {
        for (var k = pending.Count - 1; k >= 0; k--)
        {
            var p = pending[k];
            if (p.EndAt > now) continue;
            pending[k] = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            inFlight[p.Entry]--;
            Complete(p);
        }
    }

    private void Fire(int entry, double at)
    {
        var limit = runPlan.Scenarios[entry].MaxInFlight ?? runPlan.MaxConcurrency;
        if (inFlight[entry] >= limit) return;
        var profile = runProfiles[entry];

        // Decide the shape of the story first: how many calls it has, whether (and where) it fails, which call is slow.
        var spec = profile.Calls;
        var count = profile.MinCalls > 0 && profile.MinCalls < spec.Count ? rng.Next(profile.MinCalls, spec.Count + 1) : spec.Count;
        int? status = 200;
        string? error = null;
        int? pickedAt = null;
        if ((rng.NextDouble() < profile.ErrorRate || starts[entry] == profile.ForceFailOn) && profile.Errors is { Count: > 0 } errors)
        {
            var pick = Pick(errors);
            status = pick.Status;
            error = pick.Message;
            pickedAt = pick.AtCall;
        }
        var failAt = error is null ? -1 : pickedAt is { } at0 ? Math.Min(at0, count - 1) : profile.RandomFail ? rng.Next(count / 3, count) : Math.Min(profile.FailAtCall ?? count - 1, count - 1);
        var ran = failAt >= 0 ? failAt + 1 : count;
        var slowAt = rng.NextDouble() < profile.TailChance ? rng.Next(ran) : -1;

        var errorId = store.Errors.Intern(error);
        var seq = sequence++;
        var calls = new List<FakeCall>(count);
        var offset = 0.0;
        for (var i = 0; i < count; i++)
        {
            var (rawName, endpoint, rawUrl) = MaterializeCall(spec[i].Name);
            var threshold = spec[i].SlowThresholdMs;
            if (i >= ran) { calls.Add(new(i, rawName, offset, 0, null, ErrorDictionary.None, true, 0, Errors: store.Errors, Step: spec[i].Step, Rep: spec[i].Rep, Endpoint: endpoint, SlowThresholdMs: threshold, RawUrl: rawUrl)); continue; }
            var gap = i > 0 && spec[i].Pause ? Math.Round(profile.ThinkMs * (0.6 + 0.8 * rng.NextDouble()), 1) : 0;
            offset += gap;
            var d = spec[i].MedianMs * Math.Exp(spec[i].Sigma * Gaussian());
            if (i == slowAt) d *= 2.5 + rng.NextDouble() * 3;
            var isFailed = i == failAt;
            if (isFailed && status is null) d = profile.CallTimeoutMs;
            d = Math.Round(Math.Clamp(d, 3, profile.CallTimeoutMs), 1);
            calls.Add(new(i, rawName, Math.Round(offset, 1), d, isFailed ? status : 200, isFailed ? errorId : ErrorDictionary.None, false, gap, Errors: store.Errors, Step: spec[i].Step, Rep: spec[i].Rep, Endpoint: endpoint, SlowThresholdMs: threshold, RawUrl: rawUrl));
            offset += d;
        }
        var ms = Math.Round(offset, 1);
        pending.Add(new(entry, seq, at, at + ms / 1000, ms, calls, error, profile));
        inFlight[entry]++;
        starts[entry]++;
    }

    /// <summary>The bee as it stands at the stop: finished calls stay, the running call is cut off (failed, with the time it ran), later calls never ran.</summary>
    private Pending Cancel(Pending p, double at)
    {
        var cutMs = Math.Round(Math.Clamp((at - p.StartedAt) * 1000, 0, Math.Max(0, p.ElapsedMs - 0.1)), 1);
        var errorId = store.Errors.Intern(ExecutionResult.CanceledError);
        var calls = new List<FakeCall>(p.Calls.Count);
        foreach (var c in p.Calls)
        {
            if (c.Skipped || c.OffsetMs + c.DurationMs <= cutMs) calls.Add(c);
            else if (c.OffsetMs < cutMs) calls.Add(c with { DurationMs = Math.Round(cutMs - c.OffsetMs, 1), Status = null, ErrorId = errorId });
            else calls.Add(c with { DurationMs = 0, Status = null, ErrorId = ErrorDictionary.None, Skipped = true, ThinkBeforeMs = 0 });
        }
        return p with { EndAt = at, ElapsedMs = cutMs, Calls = calls, Error = ExecutionResult.CanceledError };
    }

    /// <summary>A bee finished: decide which of its calls were slow (the placeholder rule), update the counters and store it.</summary>
    private void Complete(Pending p)
    {
        var calls = new List<FakeCall>(p.Calls.Count);
        var slow = 0;
        var worst = 1.0;
        foreach (var c in p.Calls)
        {
            if (c.Skipped || c.Failed) { calls.Add(c); continue; }
            var ep = string.IsNullOrWhiteSpace(c.Endpoint) ? EndpointNormalizer.NormalizeCallName(c.Name) : c.Endpoint;
            var typical = typicals.Typical(ep);
            var isSlow = SlowCallRule.IsSlow(c.DurationMs, typical, c.SlowThresholdMs);
            typicals.Add(ep, c.DurationMs);
            if (isSlow) slow++;
            worst = Math.Max(worst, SlowCallRule.Ratio(c.DurationMs, typical, c.SlowThresholdMs));
            calls.Add(c with { Slow = isSlow, TypicalMs = typical, Endpoint = ep });
        }
        var record = new BeeRecord(p.Sequence, p.Entry, p.StartedAt, p.ElapsedMs, slow, store.Errors.Intern(p.Error), store.Errors, SlowCallRule.Encode(worst),
            p.Profile.Steps is { Count: > 0 } ? StepLogic.FailedStep(calls) : -1);
        script.BeeFinished(p.Entry, p.Sequence, p.EndAt, record.Failed, store.Bees(p.Entry).Count + 1);
        if (record.Failed) failed[p.Entry]++;
        else if (slow > 0) slowBees[p.Entry]++;
        slowCalls[p.Entry] += slow;

        var cfg = runPlan.Scenarios[p.Entry];
        var label = string.IsNullOrWhiteSpace(cfg.Label) ? cfg.ScenarioId : cfg.Label;
        var run = (id: runId, started: runStartedUtc);
        store.Complete(record, new BeeDetail(p.Sequence, p.Entry, calls, () => MakeLogs(record, label, calls, p.Profile, run.id, run.started, store.Errors)));
    }

    // ---------- fake log statements (brain 0012), generated on first use ----------

    private static List<FakeLogEntry> MakeLogs(BeeRecord bee, string label, List<FakeCall> calls, FakeProfile profile, string runId, DateTimeOffset runStarted, ErrorDictionary errors)
    {
        var rng = new Random(bee.Sequence * 31 + 17);
        var seq = bee.Sequence;
        var at = bee.StartedAt;
        var ms = bee.DurationMs;
        var error = bee.Error;
        var scope = new Dictionary<string, string> { ["RunId"] = runId, ["Scenario"] = label, ["Bee"] = $"B{seq + 1}" };
        var list = new List<FakeLogEntry>(calls.Count * 2 + 4);
        var callScopes = new Dictionary<int, IReadOnlyDictionary<string, string>>();
        // Exception texts are always interned (brain 0028); messages only where the caller knows they repeat (intern: true).
        void Log(double offsetMs, string level, string message, string? ex = null, FakeCall? call = null, bool intern = false)
        {
            IReadOnlyDictionary<string, string> s = scope;
            if (call is not null && !callScopes.TryGetValue(call.Index, out s!))
                callScopes[call.Index] = s = new Dictionary<string, string>(scope) { ["Call"] = $"{call.Index + 1}/{calls.Count} {call.Name}" };
            var t = at + offsetMs / 1000;
            list.Add(intern
                ? new(t, runStarted.AddSeconds(t), level, errors.Intern(message), errors.Intern(ex), errors, bee.Entry, seq, s, call?.Index, call?.Step ?? 0, call?.Rep ?? 0)
                : ex is null
                    ? new(t, runStarted.AddSeconds(t), level, message, null, bee.Entry, seq, s, call?.Index, call?.Step ?? 0, call?.Rep ?? 0)
                    : new(t, runStarted.AddSeconds(t), level, message, errors.Intern(ex), errors, bee.Entry, seq, s, call?.Index, call?.Step ?? 0, call?.Rep ?? 0));
        }

        Log(0, "Debug", $"B{seq + 1} started" + (calls.Count > 1 ? $" ({calls.Count} calls)" : ""));
        if (rng.NextDouble() < 0.15) Log(0.4, "Trace", "Request headers: Accept=application/json, User-Agent=Beesharp/1.0", call: calls[0], intern: true);

        foreach (var c in calls)
        {
            if (c.Skipped)
            {
                Log(ms + 0.1, "Debug", $"Skipped {c.Name}: an earlier call failed", call: c);
                continue;
            }
            if (c.ThinkBeforeMs > 0) Log(c.OffsetMs - c.ThinkBeforeMs + 0.3, "Debug", $"Think time {c.ThinkBeforeMs:0} ms", call: c);
            var end = c.OffsetMs + c.DurationMs;
            if (c.Error is null)
            {
                Log(end - 0.5, "Information", $"{c.Name} → {c.Status} in {c.DurationMs:0.#} ms", call: c);
                if (rng.NextDouble() < 0.12) Log(end - 0.6, "Trace", $"GET https://shop.example.com/api/v2/catalog/items?category=electronics&sort=popularity&filter[brand]=acme,globex,initech&filter[price][min]=10&filter[price][max]=2500&session={runId}-{seq:x8}&include=reviews,variants,related&page=3&pageSize=50", call: c);
                if (rng.NextDouble() < 0.08) Log(end - 0.55, "Debug", $"Response body ({(int)(c.DurationMs * 3)} bytes): {{\"id\":{seq + 1000},\"status\":\"ok\",\"items\":[{{\"sku\":\"A-{seq:x4}\",\"name\":\"Wireless headphones with noise cancelling\",\"price\":129.9,\"stock\":14}},{{\"sku\":\"B-{seq:x4}\",\"name\":\"USB-C charging cable, 2 m\",\"price\":9.5,\"stock\":210}}],\"links\":{{\"next\":\"/api/v2/catalog/items?page=4\"}}}}", call: c);
                if (c.Slow) Log(end - 0.2, "Warning", $"Slow response: {c.DurationMs:0} ms, typical for this call is {c.TypicalMs:0} ms", call: c);
            }
            else if (ExecutionResult.IsCanceledError(c.Error))
            {
                Log(end, "Warning", $"{c.Name} was cut off after {c.DurationMs:0.#} ms: the run was stopped", call: c, intern: true);
            }
            else if (c.Status is null)
            {
                Log(c.OffsetMs + c.DurationMs * 0.4, "Warning", "Retrying partner call (attempt 2)", call: c, intern: true);
                Log(c.OffsetMs + c.DurationMs * 0.8, "Warning", "Retrying partner call (attempt 3)", call: c, intern: true);
                Log(end, "Error", $"Timeout after {c.DurationMs:0} ms while calling https://partner.example.com/api/v1/orders/{seq + 90000}/shipping-options?country=DE&postcode=10115&weight=2.4&express=true. The partner did not answer within the configured limit.\nThe request will not be retried again; the bee stops here.",
                    $"System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of {profile.CallTimeoutMs / 1000:0} seconds elapsing.\n ---> System.TimeoutException: The operation was canceled.\n   at System.Net.Http.HttpClient.HandleFailure(Exception e, Boolean telemetryStarted, HttpResponseMessage response, CancellationTokenSource cts, CancellationToken cancellationToken, CancellationTokenSource pendingRequestsCts)\n   at Beesharp.Scenarios.CustomRequestScenario.ExecuteAsync(Object parameters, ScenarioContext context, CancellationToken ct)", c);
            }
            else
            {
                if (profile.Errors is { Count: > 1 } && c.Status == 500) Log(c.OffsetMs + c.DurationMs * 0.5, "Warning", "Retrying partner call (attempt 2)", call: c, intern: true);
                Log(end, "Error", $"{c.Name} → {c.Status} in {c.DurationMs:0.#} ms",
                    $"System.Net.Http.HttpRequestException: Response status code does not indicate success: {c.Status} ({c.Error}).\n   at Beesharp.Scenarios.UserJourney.GetAsync(String url) in UserJourney.cs:line 61\n   at Beesharp.Scenarios.UserJourney.ExecuteAsync(CancellationToken ct) in UserJourney.cs:line 38", c);
            }
        }
        Log(ms + 0.2, error is null ? "Debug" : "Information", error is null ? $"B{seq + 1} finished" : ExecutionResult.IsCanceledError(error) ? $"B{seq + 1} canceled: run stopped" : $"B{seq + 1} finished with failure: {error}");
        return list;
    }

    private FakeError Pick(IReadOnlyList<FakeError> errors)
    {
        var x = rng.NextDouble() * errors.Sum(e => e.Weight);
        foreach (var e in errors) { x -= e.Weight; if (x <= 0) return e; }
        return errors[^1];
    }

    private double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    private void Finish(double at)
    {
        // Bees still in flight when the run is stopped are kept as canceled (brain 0053), like the real runner: they keep the calls and logs
        // up to the stop, the call cut off by the stop is failed, and they look and count like failed.
        var stopped = pending.OrderBy(p => p.Sequence).ToList();
        pending.Clear();
        Array.Clear(inFlight);
        foreach (var p in stopped) Complete(Cancel(p, at));
        elapsed = at;
        script.End(runPlan.Scenarios.Count, at);
        // The shutdown program always runs at the end, also when the run was stopped (brain 0052).
        if (!string.IsNullOrWhiteSpace(runPlan.ShutdownScriptId))
            shutdown = script.RunShutdown(runPlan.ShutdownScriptId + ".cs", at);
        state = FakeRunState.Finished;
    }

    private RunSnapshot Snapshot()
    {
        var n = runPlan.Scenarios.Count;
        var labels = runPlan.Scenarios.Select(s => string.IsNullOrWhiteSpace(s.Label) ? s.ScenarioId : s.Label).ToList();
        var views = Enumerable.Range(0, n)
            .Select(i => new ScenarioView(store.Bees(i).Count, failed[i], slowBees[i], slowCalls[i], inFlight[i], starts[i]))
            .ToArray();
        var running = new IReadOnlyList<RunningBee>[n];
        for (var i = 0; i < n; i++)
            running[i] = pending.Where(p => p.Entry == i).OrderBy(p => p.Sequence).Select(p => new RunningBee(p.Sequence, p.Entry, p.StartedAt, p.Calls)).ToList();
        var steps = runProfiles.Select(p => p.DeclaredSteps).ToArray();
        return new RunSnapshot(state, elapsed, runPlan, labels, views, store, running, runtime.Snapshot(), shutdown, preflight, steps);
    }

    // ---------- the fake plan ----------

    /// <summary>Makes an entry start its bees in bursts (brain 0034), e.g. 10 bees 6 times a minute.</summary>
    private static ScenarioRunConfig Bursts(ScenarioRunConfig c, int beesPerBurst, double burstsPerMinute)
    {
        c.Pattern = SpawningPattern.Bursts;
        c.BeesPerBurst = beesPerBurst;
        c.BurstsPerMinute = burstsPerMinute;
        return c;
    }

    private ScenarioRunConfig Entry(string scenarioId, string label, double rate, string url, Action<JsonObject>? tweak = null)
    {
        var scenario = catalog.Find(scenarioId) ?? catalog.All[0];
        var c = ScenarioRunConfig.CreateFor(scenario);
        c.Label = label;
        c.RatePerMinute = rate;
        if (c.Parameters.ContainsKey("url")) c.Parameters["url"] = url;
        tweak?.Invoke(c.Parameters);
        return c;
    }

    /// <summary>Repeated calls of a story: "GET /categories/{id}" three times becomes three calls with a number, so each has its own row.</summary>
    private static IEnumerable<CallSpec> Repeat(string name, int times, double ms) =>
        Enumerable.Range(1, times).Select(i => new CallSpec($"{name} · {i}", ms));

    /// <summary>~24 calls: search, 5 product views (each with images, price and stock), 2 cart adds, the cart.</summary>
    private static readonly string[] SampleQueries =
        ["?q=running+shoes", "?q=wireless+headphones", "?q=espresso+maker", "?q=travel+backpack", "?q=mechanical+keyboard"];

    private static readonly string[] SampleSlugs =
    [
        "wireless-headphones-pro", "usb-c-charging-cable", "running-shoes-v3", "ergonomic-mouse",
        "mechanical-keyboard", "standing-desk-mat", "monitor-arm-dual", "travel-backpack-30l",
        "insulated-flask-1l", "bluetooth-speaker", "noise-canceling-buds", "smart-lamp-rgb",
        "laptop-sleeve-14", "webcam-4k-hdr", "thunderbolt-hub"
    ];

    private (string RawName, string Endpoint, string? RawUrl) MaterializeCall(string template)
    {
        var raw = template;
        var repDot = raw.LastIndexOf(" · ", StringComparison.Ordinal);
        if (repDot > 0 && int.TryParse(raw[(repDot + 3)..], out _))
            raw = raw[..repDot];

        if (raw.Contains("{slug}", StringComparison.Ordinal))
            raw = raw.Replace("{slug}", SampleSlugs[rng.Next(SampleSlugs.Length)], StringComparison.Ordinal);

        if (raw.Contains("{id}", StringComparison.Ordinal))
            raw = raw.Replace("{id}", rng.Next(1001, 9999).ToString(), StringComparison.Ordinal);

        if (raw.Contains("?q=…", StringComparison.Ordinal))
            raw = raw.Replace("?q=…", SampleQueries[rng.Next(SampleQueries.Length)], StringComparison.Ordinal);

        string? rawUrl = null;
        var space = raw.IndexOf(' ');
        if (space > 0 && raw[(space + 1)..].StartsWith('/'))
            rawUrl = "https://shop.example.com" + raw[(space + 1)..];

        var endpoint = EndpointNormalizer.NormalizeCallName(raw);
        return (raw, endpoint, rawUrl);
    }

    private static List<CallSpec> SearchStory()
    {
        var l = new List<CallSpec> { new("POST /login", 260), new("GET /search?q=…", 450, 0.2, Step: 1) };
        for (var n = 1; n <= 5; n++)
        {
            // the step "Open product" runs 5 times in a row (brain 0056): repetition n-1
            l.Add(new($"GET /products/{{id}} · {n}", 240, Step: 2, Rep: n - 1));
            l.Add(new($"GET /products/{{id}}/images · {n}", 140, 0.25, Pause: false, Step: 2, Rep: n - 1));
            l.Add(new($"GET /products/{{id}}/price · {n}", 70, 0.15, Pause: false, Step: 2, Rep: n - 1, SlowThresholdMs: 150));
            l.Add(new($"GET /products/{{id}}/availability · {n}", 110, 0.25, Pause: false, Step: 2, Rep: n - 1));
        }
        l.Add(new("POST /cart/items · 1", 320, Step: 3));
        l.Add(new("POST /cart/items · 2", 340, Step: 3));
        l.Add(new("GET /cart", 200, 0.15, Pause: false, Step: 3));
        l.Add(new("POST /payments", 700, 0.25, Step: 4, SlowThresholdMs: 1200));
        return l;
    }

    private static Action<JsonObject> urls(params string[] u) => j => j["urls"] = new JsonArray(u.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    private void LoadBlackFriday()
    {
        // Short on purpose: the demo run is 30 s, so bees are a few seconds long and rates are generous.
        Plan = new LoadPlan { DurationSeconds = 30, MaxConcurrency = 40, TimeoutSeconds = 60, ShutdownScriptId = "cleanup" };
        Profiles = [];
        void Add(ScenarioRunConfig c, FakeProfile p) { Plan.Scenarios.Add(c); Profiles.Add(p); }

        // Each entry is a whole user story; its calls are the requests of that story.
        Add(Entry("user-journey", "Checkout", 40, "",
                urls("{{BaseUrl}}/search?q=running+shoes", "{{BaseUrl}}/search?q=running+shoes&page=2", "{{BaseUrl}}/products/1042",
                     "{{BaseUrl}}/products/1042/images", "{{BaseUrl}}/products/1042/price", "{{BaseUrl}}/cart")),
            new("Log in, search, open 5 of the results (images, price, stock) and add 2 of them to the cart and pay.",
                SearchStory(), ThinkMs: 150, TailChance: 0.08, ErrorRate: 0.1,
                Errors: [new(503, "HTTP 503 Service Unavailable on GET /products/{id}", 3, AtCall: 10), new(500, "HTTP 500 Internal Server Error on GET /products/{id}/availability", 1, AtCall: 13), new(422, "HTTP 422 Payment declined", 2, AtCall: 25)],
                ForceFailOn: 2, Steps: ["Log in", "Search", "Open product", "Add to cart", "Pay"], StepTimes: [1, 1, 5, 1, 1]));
        Add(Entry("http-post-json", "Guest checkout", 30, "{{BaseUrl}}/api/checkout",
                j => j["body"] = """{ "cartId": "demo-42", "payment": "card" }"""),
            new("Check out as a guest: cart, address, shipping, payment, confirmation.",
                [new("GET /cart", 220), new("POST /checkout/address", 380, Step: 1), new("POST /checkout/shipping", 310, Step: 2), new("POST /payments", 900, 0.25, Step: 3, SlowThresholdMs: 1200), new("GET /orders/{id}/confirmation", 260, Step: 4)],
                ThinkMs: 300, TailChance: 0.05, ErrorRate: 0.12,
                Errors: [new(422, "HTTP 422 Payment declined", 3), new(500, "HTTP 500 Internal Server Error on POST /payments", 2), new(null, "Payment provider timed out after 10 s", 1)],
                FailAtCall: 3, ForceFailOn: 3, Steps: ["Cart", "Address", "Shipping", "Pay", "Confirmation"]));
        Add(Bursts(Entry("user-journey", "Browse categories", 90, "",
                urls("{{BaseUrl}}/", "{{BaseUrl}}/categories/12", "{{BaseUrl}}/categories/7", "{{BaseUrl}}/categories/31",
                     "{{BaseUrl}}/categories/31?page=2", "{{BaseUrl}}/products/918", "{{BaseUrl}}/products/204")), 15, 6),
            new("Open the home page, look through 3 categories with pagination, open 2 products.",
                [new("GET /", 200), .. Repeat("GET /categories/{id}", 3, 260), new("GET /categories/{id}?page=2", 280), new("GET /products/{id} · 1", 240), new("GET /products/{id} · 2", 240), new("GET /catalog/{slug}", 210, 0.28)],
                ThinkMs: 120, MinCalls: 5, TailChance: 0.12, ErrorRate: 0.0,
                Errors: [new(503, "HTTP 503 Service Unavailable on GET /categories/{id}", 1)], FailAtCall: 2));
        Add(Entry("http-custom", "Sign up & first order", 20, "{{BaseUrl}}/api/accounts",
                j => j["auth"] = new JsonObject { ["scheme"] = "Bearer", ["token"] = "{{ApiToken}}", ["username"] = "", ["password"] = "" }),
            new("Create an account, log in, get recommendations and place a first order.",
                [new("POST /accounts", 520), new("POST /login", 300), new("GET /recommendations", 700, 0.25, Step: 1), new("POST /cart/items", 320, Step: 1), new("POST /checkout", 1100, 0.2, Step: 2), new("GET /orders/{id}/confirmation", 280, Step: 2)],
                ThinkMs: 250, TailChance: 0.0, ErrorRate: 0.06,
                Errors: [new(500, "HTTP 500 Internal Server Error on POST /checkout", 2), new(422, "HTTP 422 Payment declined", 1)], FailAtCall: 4,
                Steps: ["Sign up and log in", "Browse and add", "Pay"]));
        Add(Entry("user-journey", "Order history & return", 20, "",
                urls("{{BaseUrl}}/login", "{{BaseUrl}}/orders", "{{BaseUrl}}/orders/88231", "{{BaseUrl}}/orders/88231/items")),
            new("Log in, look up an old order and return one item.",
                [new("POST /login", 300), new("GET /orders", 450), new("GET /orders/{id}", 380), new("GET /orders/{id}/items", 300), new("POST /returns", 850, 0.25), new("POST /returns/{id}/label", 500)],
                ThinkMs: 250, TailChance: 0.15, ErrorRate: 0.0,
                Errors: [new(502, "HTTP 502 Bad Gateway on POST /returns", 1), new(409, "HTTP 409 Item is not returnable", 1)], FailAtCall: 4));
    }

    private static readonly string[] StepsTestNames =
    [
        "Log in", "Search", "Verify the shipping address with the external address validation service", "Open product", "Add to cart",
        "Apply voucher", "Choose delivery slot", "Pay", "Wait for the payment provider to confirm the 3-D Secure challenge", "Confirm",
        "Send receipt", "Rate the order",
    ];

    private void AddStepsTest(int n)
    {
        n = Math.Clamp(n, 1, 60);
        var names = Enumerable.Range(0, n).Select(i => i < StepsTestNames.Length ? StepsTestNames[i] : $"Step {i + 1}").ToList();
        var calls = Enumerable.Range(0, n).Select(i => new CallSpec($"POST /steps/{i + 1}", 120 + 40 * (i % 4), 0.2, Step: i)).ToList();
        // failures at a few steps spread over the story, so counts show up at the start, the middle and the end
        var errors = new List<FakeError> { new(500, "HTTP 500 Internal Server Error", 3, AtCall: n / 2) };
        if (n > 1) errors.Add(new(422, "HTTP 422 Rejected", 2, AtCall: n - 1));
        if (n > 3) errors.Add(new(503, "HTTP 503 Service Unavailable", 1, AtCall: 1));
        Plan.Scenarios.Add(Entry("user-journey", $"Steps test · {n} {(n == 1 ? "step" : "steps")}", 20, "", urls("{{BaseUrl}}/")));
        Profiles.Add(new($"A story with {n} steps (debug URL ?stepcount).", calls, ThinkMs: 80, ErrorRate: 0.12, Errors: errors, Steps: names));
    }

    private FakePreset MakeSmoke()
    {
        var plan = new LoadPlan { DurationSeconds = 15, MaxConcurrency = 10, TimeoutSeconds = 60 };
        var profiles = new List<FakeProfile>();
        foreach (var i in new[] { 0, 2 })
        {
            var c = Plan.Scenarios[i].Clone();
            c.RatePerMinute = 12;
            plan.Scenarios.Add(c);
            profiles.Add(Profiles[i]);
        }
        return new("Smoke test", plan, profiles);
    }

    public void Dispose() => cts?.Cancel();
}
