using PerformanceTool.Services;
using System.Text;
using System.Text.Json.Nodes;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

// Fake data source (brain 0015): the in-browser simulator behind IRunSource. Used by the GitHub Pages demo and while the real API does not exist yet.
// It is built like the real thing has to be (brain 0021): counters are incremental, finished actors are tiny records, and calls and log
// lines are only kept for the latest actors of each scenario. WebAssembly is single threaded, so there is no locking here.

/// <summary>One call of a story: its name and typical duration. Repeated calls (e.g. five product pages) get their own entry.</summary>
/// <param name="Pause">Whether the user pauses (think time) before this call; false for calls a page fires together.</param>
public sealed record CallSpec(string Name, double MedianMs, double Sigma = 0.15, bool Pause = true);

/// <summary>
/// How a fake scenario (a whole user story, docs/context.md) behaves: its chained calls with their typical durations, think time
/// between them, failure rate and failure kinds. The actor's duration is the sum of its calls and the think times.
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
    bool RandomFail = false)
{
    public static FakeProfile Generic => new(null, [new("GET /", 150)], 0, 0.02, 0.01,
        [new(500, "HTTP 500 Internal Server Error", 1)]);
}

/// <summary>A failure kind. <see cref="Status"/> null means no response (timeout).</summary>
public sealed record FakeError(int? Status, string Message, double Weight);

public sealed record FakePreset(string Name, LoadPlan Plan, List<FakeProfile> Profiles);

/// <summary>
/// Simulates a run of a few scenarios with different rates, realistic call durations and some failures, so the UI can be
/// designed without the real runner. Actors are in flight for their duration and write fake log statements that line up with
/// their outcome. It starts out with a finished run so screenshots are never empty; <c>simulateSeconds</c> (URL <c>?simulate=3h</c>)
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
    private int[] starts = [], inFlight = [], failed = [], slowActors = [], slowCalls = [];
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

    public FakeRunSource(ScenarioCatalog catalog, double simulateSeconds = 0, RunStoreOptions? storeOptions = null)
    {
        this.catalog = catalog;
        StoreOptions = storeOptions ?? new RunStoreOptions();
        LoadBlackFriday();
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
        if (Plan.Scenarios.Count >= LoadPlan.MaxEntries) return;
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

    private static string EntryFingerprint(ScenarioRunConfig s) => $"{s.ScenarioId}|{s.Label}|{s.RatePerMinute}|{s.Pattern}|{s.ActorsPerBurst}|{s.BurstsPerMinute}|{s.Parameters.ToJsonString()}";

    /// <summary>A string that changes whenever anything the editor can change changes.</summary>
    private static string Fingerprint(LoadPlan plan)
    {
        var sb = new StringBuilder().Append(plan.DurationSeconds).Append('|').Append(plan.MaxConcurrency).Append('|').Append(plan.TimeoutSeconds);
        foreach (var s in plan.Scenarios)
            sb.Append('\n').Append(s.ScenarioId).Append('|').Append(s.Label).Append('|').Append(s.RatePerMinute).Append('|').Append(s.Pattern).Append('|').Append(s.ActorsPerBurst).Append('|').Append(s.BurstsPerMinute).Append('|').Append(s.Parameters.ToJsonString());
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

    /// <summary>Pause (brain 0027): no new actors start and the run clock stands still; actors already running finish.</summary>
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
        starts = new int[n]; inFlight = new int[n]; failed = new int[n]; slowActors = new int[n]; slowCalls = new int[n];
        // First fire at a random offset inside the first interval, like independent arrival streams.
        nextFire = runPlan.Scenarios.Select(s => rng.NextDouble() * FireInterval(s)).ToArray();
        sequence = 0;
        elapsed = 0;
        runId = "run-" + rng.Next(0x1000000).ToString("x6");
        runStartedUtc = DateTimeOffset.UtcNow;
        state = FakeRunState.Running;
    }

    /// <summary>Advances the simulation to <paramref name="now"/> seconds: fires due actors, completes finished ones.</summary>
    private void Step(double now)
    {
        if (state != FakeRunState.Running) return;
        elapsed = Math.Min(now, runPlan.DurationSeconds);
        for (var i = 0; i < runPlan.Scenarios.Count; i++)
        {
            var interval = FireInterval(runPlan.Scenarios[i]);
            var perTick = runPlan.Scenarios[i].Pattern == SpawningPattern.Bursts ? Math.Max(1, runPlan.Scenarios[i].ActorsPerBurst) : 1;
            while (nextFire[i] <= elapsed && nextFire[i] < runPlan.DurationSeconds)
            {
                // A burst starts all its actors at the same moment (brain 0034).
                for (var k = 0; k < perTick; k++) Fire(i, nextFire[i]);
                nextFire[i] += interval * (0.9 + rng.NextDouble() * 0.2);
            }
        }
        CompletePending(now);
        if (now >= runPlan.DurationSeconds && pending.Count == 0) Finish(runPlan.DurationSeconds);
    }

    /// <summary>Seconds between two fire moments: between actors (linear) or between bursts.</summary>
    private static double FireInterval(ScenarioRunConfig s) =>
        60 / Math.Max(0.1, s.Pattern == SpawningPattern.Bursts ? s.BurstsPerMinute : s.RatePerMinute);

    /// <summary>Completes the actors whose end has come. While paused the time keeps running for them only (they finish, nothing new starts).</summary>
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
        if (inFlight.Sum() >= runPlan.MaxConcurrency) return;
        var profile = runProfiles[entry];

        // Decide the shape of the story first: how many calls it has, whether (and where) it fails, which call is slow.
        var spec = profile.Calls;
        var count = profile.MinCalls > 0 && profile.MinCalls < spec.Count ? rng.Next(profile.MinCalls, spec.Count + 1) : spec.Count;
        int? status = 200;
        string? error = null;
        if ((rng.NextDouble() < profile.ErrorRate || starts[entry] == profile.ForceFailOn) && profile.Errors is { Count: > 0 } errors)
        {
            var pick = Pick(errors);
            status = pick.Status;
            error = pick.Message;
        }
        var failAt = error is null ? -1 : profile.RandomFail ? rng.Next(count / 3, count) : Math.Min(profile.FailAtCall ?? count - 1, count - 1);
        var ran = failAt >= 0 ? failAt + 1 : count;
        var slowAt = rng.NextDouble() < profile.TailChance ? rng.Next(ran) : -1;

        var errorId = store.Errors.Intern(error);
        var seq = sequence++;
        var calls = new List<FakeCall>(count);
        var offset = 0.0;
        for (var i = 0; i < count; i++)
        {
            if (i >= ran) { calls.Add(new(i, spec[i].Name, offset, 0, null, ErrorDictionary.None, true, 0, Errors: store.Errors)); continue; }
            var gap = i > 0 && spec[i].Pause ? Math.Round(profile.ThinkMs * (0.6 + 0.8 * rng.NextDouble()), 1) : 0;
            offset += gap;
            var d = spec[i].MedianMs * Math.Exp(spec[i].Sigma * Gaussian());
            if (i == slowAt) d *= 2.5 + rng.NextDouble() * 3;
            var isFailed = i == failAt;
            if (isFailed && status is null) d = profile.CallTimeoutMs;
            d = Math.Round(Math.Clamp(d, 3, profile.CallTimeoutMs), 1);
            calls.Add(new(i, spec[i].Name, Math.Round(offset, 1), d, isFailed ? status : 200, isFailed ? errorId : ErrorDictionary.None, false, gap, Errors: store.Errors));
            offset += d;
        }
        var ms = Math.Round(offset, 1);
        pending.Add(new(entry, seq, at, at + ms / 1000, ms, calls, error, profile));
        inFlight[entry]++;
        starts[entry]++;
    }

    /// <summary>An actor finished: decide which of its calls were slow (the placeholder rule), update the counters and store it.</summary>
    private void Complete(Pending p)
    {
        var calls = new List<FakeCall>(p.Calls.Count);
        var slow = 0;
        var worst = 1.0;
        foreach (var c in p.Calls)
        {
            if (c.Skipped || c.Failed) { calls.Add(c); continue; }
            var typical = typicals.Typical(p.Entry, c.Name);
            var isSlow = SlowCallRule.IsSlow(c.DurationMs, typical);
            typicals.Add(p.Entry, c.Name, c.DurationMs);
            if (isSlow) slow++;
            worst = Math.Max(worst, SlowCallRule.Ratio(c.DurationMs, typical));
            calls.Add(c with { Slow = isSlow, TypicalMs = typical });
        }
        var record = new ActorRecord(p.Sequence, p.Entry, p.StartedAt, p.ElapsedMs, slow, store.Errors.Intern(p.Error), store.Errors, SlowCallRule.Encode(worst));
        if (record.Failed) failed[p.Entry]++;
        else if (slow > 0) slowActors[p.Entry]++;
        slowCalls[p.Entry] += slow;

        var cfg = runPlan.Scenarios[p.Entry];
        var label = string.IsNullOrWhiteSpace(cfg.Label) ? cfg.ScenarioId : cfg.Label;
        var run = (id: runId, started: runStartedUtc);
        store.Complete(record, new ActorDetail(p.Sequence, p.Entry, calls, () => MakeLogs(record, label, calls, p.Profile, run.id, run.started, store.Errors)));
    }

    // ---------- fake log statements (brain 0012), generated on first use ----------

    private static List<FakeLogEntry> MakeLogs(ActorRecord actor, string label, List<FakeCall> calls, FakeProfile profile, string runId, DateTimeOffset runStarted, ErrorDictionary errors)
    {
        var rng = new Random(actor.Sequence * 31 + 17);
        var seq = actor.Sequence;
        var at = actor.StartedAt;
        var ms = actor.DurationMs;
        var error = actor.Error;
        var scope = new Dictionary<string, string> { ["RunId"] = runId, ["Scenario"] = label, ["Actor"] = $"#{seq + 1}" };
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
                ? new(t, runStarted.AddSeconds(t), level, errors.Intern(message), errors.Intern(ex), errors, actor.Entry, seq, s, call?.Index)
                : ex is null
                    ? new(t, runStarted.AddSeconds(t), level, message, null, actor.Entry, seq, s, call?.Index)
                    : new(t, runStarted.AddSeconds(t), level, message, errors.Intern(ex), errors, actor.Entry, seq, s, call?.Index));
        }

        Log(0, "Debug", $"Actor #{seq + 1} started" + (calls.Count > 1 ? $" ({calls.Count} calls)" : ""));
        if (rng.NextDouble() < 0.15) Log(0.4, "Trace", "Request headers: Accept=application/json, User-Agent=PerformanceTool/1.0", call: calls[0], intern: true);

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
            else if (c.Status is null)
            {
                Log(c.OffsetMs + c.DurationMs * 0.4, "Warning", "Retrying partner call (attempt 2)", call: c, intern: true);
                Log(c.OffsetMs + c.DurationMs * 0.8, "Warning", "Retrying partner call (attempt 3)", call: c, intern: true);
                Log(end, "Error", $"Timeout after {c.DurationMs:0} ms while calling https://partner.example.com/api/v1/orders/{seq + 90000}/shipping-options?country=DE&postcode=10115&weight=2.4&express=true. The partner did not answer within the configured limit.\nThe request will not be retried again; the actor stops here.",
                    $"System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of {profile.CallTimeoutMs / 1000:0} seconds elapsing.\n ---> System.TimeoutException: The operation was canceled.\n   at System.Net.Http.HttpClient.HandleFailure(Exception e, Boolean telemetryStarted, HttpResponseMessage response, CancellationTokenSource cts, CancellationToken cancellationToken, CancellationTokenSource pendingRequestsCts)\n   at PerformanceTool.Scenarios.CustomRequestScenario.ExecuteAsync(Object parameters, ScenarioContext context, CancellationToken ct)", c);
            }
            else
            {
                if (profile.Errors is { Count: > 1 } && c.Status == 500) Log(c.OffsetMs + c.DurationMs * 0.5, "Warning", "Retrying partner call (attempt 2)", call: c, intern: true);
                Log(end, "Error", $"{c.Name} → {c.Status} in {c.DurationMs:0.#} ms",
                    $"System.Net.Http.HttpRequestException: Response status code does not indicate success: {c.Status} ({c.Error}).\n   at PerformanceTool.Scenarios.UserJourney.GetAsync(String url) in UserJourney.cs:line 61\n   at PerformanceTool.Scenarios.UserJourney.ExecuteAsync(CancellationToken ct) in UserJourney.cs:line 38", c);
            }
        }
        Log(ms + 0.2, error is null ? "Debug" : "Information", error is null ? $"Actor #{seq + 1} finished" : $"Actor #{seq + 1} finished with failure: {error}");
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
        // Actors still in flight when the run is stopped are cancelled (no result), like the real runner.
        pending.Clear();
        Array.Clear(inFlight);
        elapsed = at;
        state = FakeRunState.Finished;
    }

    private RunSnapshot Snapshot()
    {
        var n = runPlan.Scenarios.Count;
        var labels = runPlan.Scenarios.Select(s => string.IsNullOrWhiteSpace(s.Label) ? s.ScenarioId : s.Label).ToList();
        var views = Enumerable.Range(0, n)
            .Select(i => new ScenarioView(store.Actors(i).Count, failed[i], slowActors[i], slowCalls[i], inFlight[i], starts[i]))
            .ToArray();
        var running = new IReadOnlyList<RunningActor>[n];
        for (var i = 0; i < n; i++)
            running[i] = pending.Where(p => p.Entry == i).OrderBy(p => p.Sequence).Select(p => new RunningActor(p.Sequence, p.Entry, p.StartedAt, p.Calls)).ToList();
        return new RunSnapshot(state, elapsed, runPlan, labels, views, store, running);
    }

    // ---------- the fake plan ----------

    /// <summary>Makes an entry start its actors in bursts (brain 0034), e.g. 10 actors 6 times a minute.</summary>
    private static ScenarioRunConfig Bursts(ScenarioRunConfig c, int actorsPerBurst, double burstsPerMinute)
    {
        c.Pattern = SpawningPattern.Bursts;
        c.ActorsPerBurst = actorsPerBurst;
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
    private static List<CallSpec> SearchStory()
    {
        var l = new List<CallSpec> { new("GET /search?q=…", 450, 0.2) };
        for (var n = 1; n <= 5; n++)
        {
            l.Add(new($"GET /products/{{id}} · {n}", 240));
            l.Add(new($"GET /products/{{id}}/images · {n}", 140, 0.25, Pause: false));
            l.Add(new($"GET /products/{{id}}/price · {n}", 70, 0.15, Pause: false));
            l.Add(new($"GET /products/{{id}}/availability · {n}", 110, 0.25, Pause: false));
        }
        l.Add(new("POST /cart/items · 1", 320));
        l.Add(new("POST /cart/items · 2", 340));
        l.Add(new("GET /cart", 200, 0.15, Pause: false));
        return l;
    }

    private static Action<JsonObject> urls(params string[] u) => j => j["urls"] = new JsonArray(u.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    private void LoadBlackFriday()
    {
        // Short on purpose: the demo run is 30 s, so actors are a few seconds long and rates are generous.
        Plan = new LoadPlan { DurationSeconds = 30, MaxConcurrency = 40, TimeoutSeconds = 60 };
        Profiles = [];
        void Add(ScenarioRunConfig c, FakeProfile p) { Plan.Scenarios.Add(c); Profiles.Add(p); }

        // Each entry is a whole user story; its calls are the requests of that story.
        Add(Entry("user-journey", "Search & add to cart", 40, "",
                urls("{{BaseUrl}}/search?q=running+shoes", "{{BaseUrl}}/search?q=running+shoes&page=2", "{{BaseUrl}}/products/1042",
                     "{{BaseUrl}}/products/1042/images", "{{BaseUrl}}/products/1042/price", "{{BaseUrl}}/cart")),
            new("Search for a product, open 5 of the results (images, price, stock) and add 2 of them to the cart.",
                SearchStory(), ThinkMs: 150, TailChance: 0.08, ErrorRate: 0.1,
                Errors: [new(503, "HTTP 503 Service Unavailable on POST /cart/items", 2), new(500, "HTTP 500 Internal Server Error on GET /products/{id}/availability", 1)],
                RandomFail: true, ForceFailOn: 2));
        Add(Entry("http-post-json", "Guest checkout", 30, "{{BaseUrl}}/api/checkout",
                j => j["body"] = """{ "cartId": "demo-42", "payment": "card" }"""),
            new("Check out as a guest: cart, address, shipping, payment, confirmation.",
                [new("GET /cart", 220), new("POST /checkout/address", 380), new("POST /checkout/shipping", 310), new("POST /payments", 900, 0.25), new("GET /orders/{id}/confirmation", 260)],
                ThinkMs: 300, TailChance: 0.05, ErrorRate: 0.12,
                Errors: [new(422, "HTTP 422 Payment declined", 3), new(500, "HTTP 500 Internal Server Error on POST /payments", 2), new(null, "Payment provider timed out after 10 s", 1)],
                FailAtCall: 3, ForceFailOn: 3));
        Add(Bursts(Entry("user-journey", "Browse categories", 90, "",
                urls("{{BaseUrl}}/", "{{BaseUrl}}/categories/12", "{{BaseUrl}}/categories/7", "{{BaseUrl}}/categories/31",
                     "{{BaseUrl}}/categories/31?page=2", "{{BaseUrl}}/products/918", "{{BaseUrl}}/products/204")), 15, 6),
            new("Open the home page, look through 3 categories with pagination, open 2 products.",
                [new("GET /", 200), .. Repeat("GET /categories/{id}", 3, 260), new("GET /categories/{id}?page=2", 280), new("GET /products/{id} · 1", 240), new("GET /products/{id} · 2", 240)],
                ThinkMs: 120, MinCalls: 5, TailChance: 0.12, ErrorRate: 0.0,
                Errors: [new(503, "HTTP 503 Service Unavailable on GET /categories/{id}", 1)], FailAtCall: 2));
        Add(Entry("http-custom", "Sign up & first order", 20, "{{BaseUrl}}/api/accounts",
                j => j["auth"] = new JsonObject { ["scheme"] = "Bearer", ["token"] = "{{ApiToken}}", ["username"] = "", ["password"] = "" }),
            new("Create an account, log in, get recommendations and place a first order.",
                [new("POST /accounts", 520), new("POST /login", 300), new("GET /recommendations", 700, 0.25), new("POST /cart/items", 320), new("POST /checkout", 1100, 0.2), new("GET /orders/{id}/confirmation", 280)],
                ThinkMs: 250, TailChance: 0.0, ErrorRate: 0.0,
                Errors: [new(500, "HTTP 500 Internal Server Error on POST /checkout", 2), new(422, "HTTP 422 Payment declined", 1)], FailAtCall: 4));
        Add(Entry("user-journey", "Order history & return", 20, "",
                urls("{{BaseUrl}}/login", "{{BaseUrl}}/orders", "{{BaseUrl}}/orders/88231", "{{BaseUrl}}/orders/88231/items")),
            new("Log in, look up an old order and return one item.",
                [new("POST /login", 300), new("GET /orders", 450), new("GET /orders/{id}", 380), new("GET /orders/{id}/items", 300), new("POST /returns", 850, 0.25), new("POST /returns/{id}/label", 500)],
                ThinkMs: 250, TailChance: 0.15, ErrorRate: 0.0,
                Errors: [new(502, "HTTP 502 Bad Gateway on POST /returns", 1), new(409, "HTTP 409 Item is not returnable", 1)], FailAtCall: 4));
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
