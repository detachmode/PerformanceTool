using System.Text.Json.Nodes;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Services.Drafts;

// DRAFT ONLY (docs/plan.md "UI drafts"): fake data for the /drafts pages. Delete or promote with Components/Drafts.

public enum FakeRunState { Idle, Running, Finished }

/// <summary>How a fake scenario behaves: latency distribution, failure rate and failure kinds.</summary>
public sealed record FakeProfile(
    string? Description,
    double MedianMs,
    double Sigma,
    double TailChance = 0,
    double ErrorRate = 0,
    long BytesMedian = 20_000,
    IReadOnlyList<FakeError>? Errors = null,
    string Method = "GET",
    string Path = "/",
    int Steps = 1)
{
    public static FakeProfile Generic => new(null, 150, 0.4, 0.02, 0.01, 8_000,
        [new(500, "HTTP 500 Internal Server Error", 1)]);
}

/// <summary>A failure kind. <see cref="Status"/> null means no response (timeout).</summary>
public sealed record FakeError(int? Status, string Message, double Weight);

public sealed record FakePreset(string Name, LoadPlan Plan, List<FakeProfile> Profiles);

/// <summary>One log statement of one execution (brain 0012): level, message, optional exception and the execution's scope values.</summary>
public sealed record FakeLogEntry(
    long Id,
    double AtSeconds,
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception,
    int Entry,
    int Sequence,
    IReadOnlyDictionary<string, string> Scope)
{
    public static readonly string[] Levels = ["Trace", "Debug", "Information", "Warning", "Error"];
    public static int Rank(string level) => Array.IndexOf(Levels, level);
}

/// <summary>Immutable view of the run for the pages. Arrays are copies, so rendering never races the simulation.</summary>
public sealed record RunSnapshot(
    FakeRunState State,
    double Elapsed,
    LoadPlan Plan,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string?> Descriptions,
    IReadOnlyList<ExecutionResult> All,
    IReadOnlyList<ExecutionResult>[] ByEntry,
    IReadOnlyList<FakeLogEntry>[] LogsByEntry,
    int[] InFlight,
    int[] Starts,
    int[] Dropped,
    double?[] SinceLastStart)
{
    public bool Running => State == FakeRunState.Running;
    public int Count => Plan.Scenarios.Count;
}

/// <summary>
/// Simulates a run of a few scenarios with different rates, realistic latencies and some failures, so the UI can be
/// designed without the real runner. Executions are in flight for their duration (tiles light up) and write fake log
/// statements that line up with their outcome. It starts out with a finished run so screenshots are never empty.
/// Scoped: one simulation per circuit.
/// </summary>
public sealed class FakeRunSimulator : IDisposable
{
    private readonly ScenarioCatalog catalog;
    private readonly object gate = new();
    private readonly Random rng = new(7);
    private CancellationTokenSource? cts;

    // The editable mix (what the editor page changes) and the profile of each entry.
    public LoadPlan Plan { get; private set; } = new();
    public List<FakeProfile> Profiles { get; private set; } = [];
    public string PresetName { get; set; } = "Black Friday mix";
    public List<FakePreset> Presets { get; } = [];

    // Run state (guarded by gate).
    private LoadPlan runPlan = new();
    private List<FakeProfile> runProfiles = [];
    private FakeRunState state = FakeRunState.Idle;
    private double elapsed;
    private readonly List<ExecutionResult> all = [];
    private List<ExecutionResult>[] byEntry = [];
    private List<FakeLogEntry>[] logs = [];
    private int[] starts = [], dropped = [], inFlight = [];
    private double[] lastStart = [], nextFire = [];
    private readonly List<Pending> pending = [];
    private int sequence;
    private long logId;
    private string runId = "";
    private DateTimeOffset runStartedUtc;

    private sealed record Pending(int Entry, double EndAt, ExecutionResult Result, List<FakeLogEntry> Logs);

    public RunSnapshot Current { get; private set; } = default!;
    public bool IsRunning => Current.Running;

    /// <summary>Raised on the simulation thread whenever <see cref="Current"/> changes. Pages must marshal with InvokeAsync.</summary>
    public event Action? Changed;

    public FakeRunSimulator(ScenarioCatalog catalog)
    {
        this.catalog = catalog;
        LoadBlackFriday();
        Presets.Add(new("Black Friday mix", Plan.Clone(), [.. Profiles]));
        Presets.Add(MakeSmoke());
        // Pre-seed a finished run, simulated in virtual time.
        BeginRun();
        lock (gate) { runStartedUtc = DateTimeOffset.UtcNow.AddSeconds(-Plan.DurationSeconds - 20); }
        for (var t = 0.05; t < Plan.DurationSeconds + 6; t += 0.05) { Step(t); if (state != FakeRunState.Running) break; }
        lock (gate) { FinishLocked(Plan.DurationSeconds); }
        Current = Snapshot();
    }

    // ---------- editing the mix (in memory) ----------

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
    }

    public void LoadPreset(string name)
    {
        if (Presets.FirstOrDefault(p => p.Name == name) is not { } p) return;
        Plan = p.Plan.Clone();
        Profiles = [.. p.Profiles];
        PresetName = p.Name;
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

    public void Stop()
    {
        cts?.Cancel();
        lock (gate)
        {
            if (state == FakeRunState.Running) FinishLocked(Math.Min(elapsed, runPlan.DurationSeconds));
        }
        Current = Snapshot();
        Changed?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Step(clock.Elapsed.TotalSeconds);
                var done = state != FakeRunState.Running;
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
        lock (gate)
        {
            runPlan = Plan.Clone();
            runProfiles = [.. Profiles];
            var n = runPlan.Scenarios.Count;
            all.Clear();
            pending.Clear();
            byEntry = Enumerable.Range(0, n).Select(_ => new List<ExecutionResult>()).ToArray();
            logs = Enumerable.Range(0, n).Select(_ => new List<FakeLogEntry>()).ToArray();
            starts = new int[n]; dropped = new int[n]; inFlight = new int[n];
            lastStart = Enumerable.Repeat(double.NaN, n).ToArray();
            // First fire at a random offset inside the first interval, like independent arrival streams.
            nextFire = runPlan.Scenarios.Select(s => rng.NextDouble() * 60 / Math.Max(0.1, s.RatePerMinute)).ToArray();
            sequence = 0;
            logId = 0;
            elapsed = 0;
            runId = "run-" + rng.Next(0x1000000).ToString("x6");
            runStartedUtc = DateTimeOffset.UtcNow;
            state = FakeRunState.Running;
        }
    }

    /// <summary>Advances the simulation to <paramref name="now"/> seconds: fires due executions, completes finished ones.</summary>
    private void Step(double now)
    {
        lock (gate)
        {
            if (state != FakeRunState.Running) return;
            elapsed = Math.Min(now, runPlan.DurationSeconds);
            for (var i = 0; i < runPlan.Scenarios.Count; i++)
            {
                var interval = 60 / Math.Max(0.1, runPlan.Scenarios[i].RatePerMinute);
                while (nextFire[i] <= elapsed && nextFire[i] < runPlan.DurationSeconds)
                {
                    Fire(i, nextFire[i]);
                    nextFire[i] += interval * (0.9 + rng.NextDouble() * 0.2);
                }
            }
            for (var k = pending.Count - 1; k >= 0; k--)
            {
                var p = pending[k];
                if (p.EndAt > now) continue;
                pending.RemoveAt(k);
                inFlight[p.Entry]--;
                all.Add(p.Result);
                byEntry[p.Entry].Add(p.Result);
                foreach (var l in p.Logs) logs[p.Entry].Add(l with { Id = logId++ });
            }
            if (now >= runPlan.DurationSeconds && pending.Count == 0) FinishLocked(runPlan.DurationSeconds);
        }
    }

    private void Fire(int entry, double at)
    {
        if (inFlight.Sum() >= runPlan.MaxConcurrency) { dropped[entry]++; return; }
        var profile = runProfiles[entry];
        var timeoutMs = runPlan.TimeoutSeconds * 1000.0;
        var ms = profile.MedianMs * Math.Exp(profile.Sigma * Gaussian());
        var slow = rng.NextDouble() < profile.TailChance;
        if (slow) ms *= 2.5 + rng.NextDouble() * 3;
        ms = Math.Clamp(ms, 3, timeoutMs);

        int? status = 200;
        string? error = null;
        if (rng.NextDouble() < profile.ErrorRate && profile.Errors is { Count: > 0 } errors)
        {
            var pick = Pick(errors);
            status = pick.Status;
            error = pick.Message;
            if (status is null) ms = timeoutMs;
        }
        var bytes = status is null ? 0 : (long)(profile.BytesMedian * (0.8 + rng.NextDouble() * 0.4));
        var seq = sequence++;
        ms = Math.Round(ms, 1);
        var result = new ExecutionResult(seq, entry, at, ms, status, bytes, error);
        pending.Add(new(entry, at + ms / 1000, result, MakeLogs(entry, seq, at, ms, status, error, slow, profile, timeoutMs)));
        inFlight[entry]++;
        starts[entry]++;
        lastStart[entry] = at;
    }

    // ---------- fake log statements (brain 0012) ----------

    private List<FakeLogEntry> MakeLogs(int entry, int seq, double at, double ms, int? status, string? error, bool slow, FakeProfile profile, double timeoutMs)
    {
        var cfg = runPlan.Scenarios[entry];
        var label = string.IsNullOrWhiteSpace(cfg.Label) ? cfg.ScenarioId : cfg.Label;
        var scope = new Dictionary<string, string> { ["RunId"] = runId, ["Scenario"] = label, ["Execution"] = $"#{seq + 1}" };
        var list = new List<FakeLogEntry>();
        void Log(double offsetMs, string level, string message, string? ex = null) =>
            list.Add(new(0, at + offsetMs / 1000, runStartedUtc.AddSeconds(at + offsetMs / 1000), level, message, ex, entry, seq, scope));

        var request = $"{profile.Method} {profile.Path}";
        Log(0, "Debug", $"Execution #{seq + 1} started");
        if (rng.NextDouble() < 0.15) Log(0.4, "Trace", "Request headers: Accept=application/json, User-Agent=PerformanceTool/1.0");

        if (profile.Steps > 1)
        {
            // User journey: one line per step that completed; the failing step carries the error.
            var failedStep = error is null ? 0 : 3; // 1-based; matches "Step 3 failed" in the error text
            var per = ms / profile.Steps;
            for (var s = 1; s <= profile.Steps; s++)
            {
                var stepMs = per * (0.7 + rng.NextDouble() * 0.6);
                var path = StepPaths[Math.Min(s - 1, StepPaths.Length - 1)];
                if (s == failedStep)
                {
                    Log(per * s, "Error", $"Step {s}/{profile.Steps} GET {path} → {status} in {stepMs:0} ms",
                        $"System.Net.Http.HttpRequestException: Response status code does not indicate success: {status}.\n   at PerformanceTool.Scenarios.UserJourney.GetAsync(String url) in UserJourney.cs:line 61\n   at PerformanceTool.Scenarios.UserJourney.ExecuteAsync(CancellationToken ct) in UserJourney.cs:line 38");
                    break;
                }
                Log(per * s, "Information", $"Step {s}/{profile.Steps} GET {path} → 200 in {stepMs:0} ms");
                if (s < profile.Steps) Log(per * s + 0.3, "Debug", "Think time 400 ms");
            }
        }
        else if (error is null)
        {
            Log(ms - 0.5, "Information", $"{request} → {status} in {ms:0.#} ms");
            if (slow && ms > profile.MedianMs * 2.5) Log(ms - 0.2, "Warning", $"Slow response: {ms:0} ms (median {profile.MedianMs:0} ms)");
        }
        else if (status is null)
        {
            Log(ms * 0.4, "Warning", "Retrying partner call (attempt 2)");
            Log(ms * 0.8, "Warning", "Retrying partner call (attempt 3)");
            Log(ms, "Error", $"Timeout after {timeoutMs:0} ms",
                $"System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of {timeoutMs / 1000:0} seconds elapsing.\n ---> System.TimeoutException: The operation was canceled.\n   at System.Net.Http.HttpClient.HandleFailure(Exception e, Boolean telemetryStarted, HttpResponseMessage response, CancellationTokenSource cts, CancellationToken cancellationToken, CancellationTokenSource pendingRequestsCts)\n   at PerformanceTool.Scenarios.CustomRequestScenario.ExecuteAsync(Object parameters, ScenarioContext context, CancellationToken ct)");
        }
        else
        {
            if (profile.Errors is { Count: > 1 } && status == 500) Log(ms * 0.5, "Warning", "Retrying partner call (attempt 2)");
            Log(ms, "Error", $"{request} → {status} in {ms:0.#} ms",
                $"System.Net.Http.HttpRequestException: Response status code does not indicate success: {status} ({error}).\n   at System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode()\n   at PerformanceTool.Scenarios.HttpScenarioBase.SendAsync(HttpRequestMessage request, CancellationToken ct)");
        }
        Log(ms + 0.2, error is null ? "Debug" : "Information", error is null ? $"Execution #{seq + 1} finished" : $"Execution #{seq + 1} finished with failure: {error}");
        return list;
    }

    private static readonly string[] StepPaths = ["/", "/products/42", "/cart", "/checkout"];

    private FakeError Pick(IReadOnlyList<FakeError> errors)
    {
        var x = rng.NextDouble() * errors.Sum(e => e.Weight);
        foreach (var e in errors) { x -= e.Weight; if (x <= 0) return e; }
        return errors[^1];
    }

    private double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    private void FinishLocked(double at)
    {
        // Executions still in flight when the run is stopped are cancelled (no result), like the real runner.
        pending.Clear();
        Array.Clear(inFlight);
        elapsed = at;
        state = FakeRunState.Finished;
    }

    private RunSnapshot Snapshot()
    {
        lock (gate)
        {
            var n = runPlan.Scenarios.Count;
            var labels = runPlan.Scenarios.Select(s => string.IsNullOrWhiteSpace(s.Label) ? s.ScenarioId : s.Label).ToList();
            var descriptions = Enumerable.Range(0, n).Select(i => runProfiles[i].Description ?? catalog.Find(runPlan.Scenarios[i].ScenarioId)?.Description).ToList();
            var sinceLast = lastStart.Select(t => double.IsNaN(t) || state != FakeRunState.Running ? (double?)null : Math.Max(0, elapsed - t)).ToArray();
            return new RunSnapshot(state, elapsed, runPlan, labels, descriptions, all.ToArray(),
                byEntry.Select(l => (IReadOnlyList<ExecutionResult>)l.ToArray()).ToArray(),
                logs.Select(l => (IReadOnlyList<FakeLogEntry>)l.ToArray()).ToArray(),
                (int[])inFlight.Clone(), (int[])starts.Clone(), (int[])dropped.Clone(), sinceLast);
        }
    }

    // ---------- the fake mix ----------

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

    private void LoadBlackFriday()
    {
        Plan = new LoadPlan { DurationSeconds = 60, MaxConcurrency = 50, TimeoutSeconds = 3 };
        Profiles = [];
        void Add(ScenarioRunConfig c, FakeProfile p) { Plan.Scenarios.Add(c); Profiles.Add(p); }

        Add(Entry("http-get", "Product list GET", 600, "https://shop.example.com/api/products?page=1"),
            new("Browse the catalogue: the hottest read path of the shop.", 85, 0.35, 0.02, 0.003, 48_000,
                [new(503, "HTTP 503 Service Unavailable", 1)], "GET", "/api/products?page=1"));
        Add(Entry("http-post-json", "Checkout POST", 120, "https://shop.example.com/api/checkout",
                j => j["body"] = """{ "cartId": "demo-42", "payment": "card" }"""),
            new("Places an order with a fixed demo cart.", 240, 0.45, 0.03, 0.015, 1_200,
                [new(500, "HTTP 500 Internal Server Error", 2), new(422, "HTTP 422 Payment declined", 1)], "POST", "/api/checkout"));
        Add(Entry("http-get", "Search", 300, "https://shop.example.com/api/search?q=shoes"),
            new("Full-text search for a popular term.", 140, 0.5, 0.04, 0.005, 22_000,
                [new(500, "HTTP 500 Internal Server Error", 1)], "GET", "/api/search?q=shoes"));
        Add(Entry("user-journey", "User journey", 40, ""),
            new("Home, product, cart and checkout pages with think time.", 1700, 0.22, 0.02, 0.02, 160_000,
                [new(502, "Step 3 failed: HTTP 502 Bad Gateway", 1)], "GET", "/", 4));
        Add(Entry("http-custom", "Flaky partner API", 90, "https://partner.example.com/v2/stock"),
            new("Third-party stock service that fails and times out under load.", 320, 0.6, 0.05, 0.13, 3_400,
                [new(500, "HTTP 500 Internal Server Error", 5), new(null, "Timed out after 3 s", 3), new(503, "HTTP 503 Service Unavailable", 2)],
                "GET", "/v2/stock"));
    }

    private FakePreset MakeSmoke()
    {
        var plan = new LoadPlan { DurationSeconds = 20, MaxConcurrency = 10, TimeoutSeconds = 5 };
        var profiles = new List<FakeProfile>();
        foreach (var i in new[] { 0, 2 })
        {
            var c = Plan.Scenarios[i].Clone();
            c.RatePerMinute = 30;
            plan.Scenarios.Add(c);
            profiles.Add(Profiles[i]);
        }
        return new("Smoke test", plan, profiles);
    }

    public void Dispose() => cts?.Cancel();
}
