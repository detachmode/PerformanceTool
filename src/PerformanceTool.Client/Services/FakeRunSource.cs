using PerformanceTool.Services;
using System.Text.Json.Nodes;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

// Fake data source (brain 0015): the in-browser simulator behind IRunSource. Used by the GitHub Pages demo and while the real API does not exist yet.

/// <summary>One call of a story: its name and typical duration. Repeated calls (e.g. five product pages) get their own entry.</summary>
/// <summary>
/// <paramref name="Group"/> bundles related calls (e.g. "Images") so a 50-call instance can be coloured by a handful of groups.
/// <paramref name="Pause"/>: whether the user pauses (think time) before this call; false for calls a page fires together.
/// </summary>
public sealed record CallSpec(string Name, double MedianMs, double Sigma = 0.35, string? Group = null, bool Pause = true);

/// <summary>
/// How a fake scenario (a whole user story, docs/context.md) behaves: its chained calls with their typical durations, think time
/// between them, failure rate and failure kinds. The instance's duration is the sum of its calls and the think times.
/// </summary>
public sealed record FakeProfile(
    string? Description,
    IReadOnlyList<CallSpec> Calls,
    double ThinkMs = 0,
    double TailChance = 0,
    double ErrorRate = 0,
    long BytesMedian = 20_000,
    IReadOnlyList<FakeError>? Errors = null,
    int MinCalls = 0,
    int? FailAtCall = null,
    int? SpikeCall = null,
    double SpikeChance = 0,
    int? ForceFailOn = null,
    double CallTimeoutMs = 10_000,
    bool RandomFail = false)
{
    public static FakeProfile Generic => new(null, [new("GET /", 150)], 0, 0.02, 0.01, 8_000,
        [new(500, "HTTP 500 Internal Server Error", 1)]);
}

/// <summary>A failure kind. <see cref="Status"/> null means no response (timeout).</summary>
public sealed record FakeError(int? Status, string Message, double Weight);

public sealed record FakePreset(string Name, LoadPlan Plan, List<FakeProfile> Profiles);

/// <summary>
/// Simulates a run of a few scenarios with different rates, realistic latencies and some failures, so the UI can be
/// designed without the real runner. Instances are in flight for their duration (tiles light up) and write fake log
/// statements that line up with their outcome. It starts out with a finished run so screenshots are never empty.
/// One simulation per browser tab (singleton in the WebAssembly app).
/// </summary>
public sealed class FakeRunSource : IRunSource
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
    public IReadOnlyList<string> PresetNames => Presets.Select(p => p.Name).ToList();

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

    private sealed record Pending(int Entry, double EndAt, ExecutionResult Result, List<FakeLogEntry> Logs, List<FakeCall> Calls);
    // Calls by instance number: append-only, so snapshots can share it. A new run gets a new dictionary.
    private System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<FakeCall>> calls = new();

    public RunSnapshot Current { get; private set; } = default!;
    public bool IsRunning => Current.Running;

    /// <summary>DRAFT only: how much faster the simulated clock runs than the wall clock (1, 4 or 10). Can change during a run.</summary>
    private volatile int demoSpeed = 1;
    public int DemoSpeed { get => demoSpeed; set => demoSpeed = Math.Max(1, value); }

    /// <summary>Raised on the simulation thread whenever <see cref="Current"/> changes. Pages must marshal with InvokeAsync.</summary>
    public event Action? Changed;

    public FakeRunSource(ScenarioCatalog catalog)
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

    public void DuplicateEntry(int index)
    {
        if (index < 0 || index >= Plan.Scenarios.Count || Plan.Scenarios.Count >= LoadPlan.MaxEntries) return;
        var copy = Plan.Scenarios[index].Clone();
        copy.Label = string.IsNullOrWhiteSpace(copy.Label) ? copy.ScenarioId + " copy" : copy.Label + " copy";
        Plan.Scenarios.Insert(index + 1, copy);
        Profiles.Insert(index + 1, Profiles[index]);
    }

    /// <summary>Moves an entry by <paramref name="delta"/> places (negative = up). Returns the new index.</summary>
    public int MoveEntry(int index, int delta)
    {
        var to = Math.Clamp(index + delta, 0, Plan.Scenarios.Count - 1);
        if (index < 0 || index >= Plan.Scenarios.Count || to == index) return index;
        var c = Plan.Scenarios[index]; Plan.Scenarios.RemoveAt(index); Plan.Scenarios.Insert(to, c);
        var p = Profiles[index]; Profiles.RemoveAt(index); Profiles.Insert(to, p);
        return to;
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
        // Simulated time advances by the wall-clock delta times the demo speed (the speed may change mid-run).
        double virtualNow = 0, lastWall = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var wall = clock.Elapsed.TotalSeconds;
                virtualNow += (wall - lastWall) * demoSpeed;
                lastWall = wall;
                Step(virtualNow);
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
            calls = new();
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

    /// <summary>Advances the simulation to <paramref name="now"/> seconds: fires due instances, completes finished ones.</summary>
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
                calls[p.Result.Sequence] = p.Calls;
                foreach (var l in p.Logs) logs[p.Entry].Add(l with { Id = logId++ });
            }
            if (now >= runPlan.DurationSeconds && pending.Count == 0) FinishLocked(runPlan.DurationSeconds);
        }
    }

    private void Fire(int entry, double at)
    {
        if (inFlight.Sum() >= runPlan.MaxConcurrency) { dropped[entry]++; return; }
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
        var spikeAt = profile.SpikeCall is { } sc && sc < ran && rng.NextDouble() < profile.SpikeChance ? sc : -1;

        var seq = sequence++;
        var calls = new List<FakeCall>();
        var offset = 0.0;
        for (var i = 0; i < count; i++)
        {
            if (i >= ran) { calls.Add(new(seq, i, spec[i].Name, offset, 0, null, null, true, 0, spec[i].Group)); continue; }
            var gap = i > 0 && spec[i].Pause ? Math.Round(profile.ThinkMs * (0.6 + 0.8 * rng.NextDouble()), 1) : 0;
            offset += gap;
            var d = spec[i].MedianMs * Math.Exp(spec[i].Sigma * Gaussian());
            if (i == slowAt) d *= 2.5 + rng.NextDouble() * 3;
            if (i == spikeAt) d += spec[i].MedianMs * 3;
            var failed = i == failAt;
            if (failed && status is null) d = profile.CallTimeoutMs;
            d = Math.Round(Math.Clamp(d, 3, profile.CallTimeoutMs), 1);
            calls.Add(new(seq, i, spec[i].Name, Math.Round(offset, 1), d, failed ? status : 200, failed ? error : null, false, gap, spec[i].Group));
            offset += d;
        }
        var ms = Math.Round(offset, 1);
        var bytes = status is null ? 0 : (long)(profile.BytesMedian * (0.8 + rng.NextDouble() * 0.4));
        var result = new ExecutionResult(seq, entry, at, ms, status, bytes, error);
        pending.Add(new(entry, at + ms / 1000, result, MakeLogs(entry, seq, at, ms, status, error, slowAt >= 0, profile, calls), calls));
        inFlight[entry]++;
        starts[entry]++;
        lastStart[entry] = at;
    }

    // ---------- fake log statements (brain 0012) ----------

    private List<FakeLogEntry> MakeLogs(int entry, int seq, double at, double ms, int? status, string? error, bool slow, FakeProfile profile, List<FakeCall> calls)
    {
        var cfg = runPlan.Scenarios[entry];
        var label = string.IsNullOrWhiteSpace(cfg.Label) ? cfg.ScenarioId : cfg.Label;
        var scope = new Dictionary<string, string> { ["RunId"] = runId, ["Scenario"] = label, ["Actor"] = $"#{seq + 1}" };
        var list = new List<FakeLogEntry>();
        void Log(double offsetMs, string level, string message, string? ex = null, FakeCall? call = null)
        {
            var s = scope;
            if (call is not null) s = new Dictionary<string, string>(scope) { ["Call"] = $"{call.Index + 1}/{calls.Count} {call.Name}" };
            list.Add(new(0, at + offsetMs / 1000, runStartedUtc.AddSeconds(at + offsetMs / 1000), level, message, ex, entry, seq, s, call?.Index));
        }

        Log(0, "Debug", $"Actor #{seq + 1} started" + (calls.Count > 1 ? $" ({calls.Count} calls)" : ""));
        if (rng.NextDouble() < 0.15) Log(0.4, "Trace", "Request headers: Accept=application/json, User-Agent=PerformanceTool/1.0", call: calls[0]);

        var journey = calls.Count > 1;
        var slowest = calls.Where(c => !c.Skipped).MaxBy(c => c.DurationMs);
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
                if (slow && c == slowest) Log(end - 0.2, "Warning", $"Slow response: {c.DurationMs:0} ms", call: c);
            }
            else if (c.Status is null)
            {
                Log(c.OffsetMs + c.DurationMs * 0.4, "Warning", "Retrying partner call (attempt 2)", call: c);
                Log(c.OffsetMs + c.DurationMs * 0.8, "Warning", "Retrying partner call (attempt 3)", call: c);
                Log(end, "Error", $"Timeout after {c.DurationMs:0} ms",
                    $"System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of {profile.CallTimeoutMs / 1000:0} seconds elapsing.\n ---> System.TimeoutException: The operation was canceled.\n   at System.Net.Http.HttpClient.HandleFailure(Exception e, Boolean telemetryStarted, HttpResponseMessage response, CancellationTokenSource cts, CancellationToken cancellationToken, CancellationTokenSource pendingRequestsCts)\n   at PerformanceTool.Scenarios.CustomRequestScenario.ExecuteAsync(Object parameters, ScenarioContext context, CancellationToken ct)", c);
            }
            else
            {
                if (profile.Errors is { Count: > 1 } && c.Status == 500) Log(c.OffsetMs + c.DurationMs * 0.5, "Warning", "Retrying partner call (attempt 2)", call: c);
                var where = journey
                    ? "   at PerformanceTool.Scenarios.UserJourney.GetAsync(String url) in UserJourney.cs:line 61\n   at PerformanceTool.Scenarios.UserJourney.ExecuteAsync(CancellationToken ct) in UserJourney.cs:line 38"
                    : "   at System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode()\n   at PerformanceTool.Scenarios.HttpScenarioBase.SendAsync(HttpRequestMessage request, CancellationToken ct)";
                Log(end, "Error", $"{c.Name} → {c.Status} in {c.DurationMs:0.#} ms",
                    $"System.Net.Http.HttpRequestException: Response status code does not indicate success: {c.Status} ({c.Error}).\n{where}", c);
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

    private void FinishLocked(double at)
    {
        // Instances still in flight when the run is stopped are cancelled (no result), like the real runner.
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
                (int[])inFlight.Clone(), (int[])starts.Clone(), (int[])dropped.Clone(), sinceLast, calls);
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

    /// <summary>Repeated calls of a story: "GET /products/{id}" five times becomes five calls with a number, so each has its own row.</summary>
    private static IEnumerable<CallSpec> Repeat(string name, int times, double ms, double sigma = 0.35) =>
        Enumerable.Range(1, times).Select(i => new CallSpec($"{name} · {i}", ms, sigma));

    /// <summary>~48 calls: search, 4 more result pages, 10 product views (each with images, price and stock), 2 cart adds, the cart.</summary>
    private static List<CallSpec> SearchStory()
    {
        var l = new List<CallSpec> { new("GET /search?q=…", 450, 0.4, "Search") };
        var n = 0;
        for (var page = 1; page <= 5; page++)
        {
            if (page > 1) l.Add(new($"GET /search?q=…&page={page}", 320, 0.35, "Results"));
            for (var v = 0; v < 2; v++)
            {
                n++;
                l.Add(new($"GET /products/{{id}} · {n}", 240, 0.35, "Product"));
                l.Add(new($"GET /products/{{id}}/images · {n}", 140, 0.5, "Images", Pause: false));
                l.Add(new($"GET /products/{{id}}/price · {n}", 70, 0.3, "Price & stock", Pause: false));
                l.Add(new($"GET /products/{{id}}/availability · {n}", 110, 0.45, "Price & stock", Pause: false));
            }
        }
        l.Add(new("POST /cart/items · 1", 320, 0.35, "Cart"));
        l.Add(new("POST /cart/items · 2", 340, 0.35, "Cart"));
        l.Add(new("GET /cart", 200, 0.3, "Cart", Pause: false));
        return l;
    }

    private static Action<JsonObject> urls(params string[] u) => j => j["urls"] = new JsonArray(u.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    private void LoadBlackFriday()
    {
        Plan = new LoadPlan { DurationSeconds = 120, MaxConcurrency = 40, TimeoutSeconds = 60 };
        Profiles = [];
        void Add(ScenarioRunConfig c, FakeProfile p) { Plan.Scenarios.Add(c); Profiles.Add(p); }

        // Each entry is a whole user story; its calls are the requests of that story.
        Add(Entry("user-journey", "Search & add to cart", 12, "",
                urls("https://shop.example.com/search?q=running+shoes", "https://shop.example.com/search?q=running+shoes&page=2", "https://shop.example.com/products/1042",
                     "https://shop.example.com/products/1042/images", "https://shop.example.com/products/1042/price", "https://shop.example.com/cart")),
            new("Search for a product, page through 5 result pages, open 10 products (images, price, stock) and add 2 of them to the cart.",
                SearchStory(), ThinkMs: 1100, TailChance: 0.06, ErrorRate: 0.1, BytesMedian: 640_000,
                Errors: [new(503, "HTTP 503 Service Unavailable on POST /cart/items", 2), new(500, "HTTP 500 Internal Server Error on GET /products/{id}/availability", 1)],
                RandomFail: true, SpikeCall: 45, SpikeChance: 0.15, ForceFailOn: 2));
        Add(Entry("http-post-json", "Guest checkout", 12, "https://shop.example.com/api/checkout",
                j => j["body"] = """{ "cartId": "demo-42", "payment": "card" }"""),
            new("Check out as a guest: cart, address, shipping, payment, confirmation.",
                [new("GET /cart", 220), new("POST /checkout/address", 380), new("POST /checkout/shipping", 310), new("POST /payments", 900, 0.5), new("GET /orders/{id}/confirmation", 260)],
                ThinkMs: 2500, TailChance: 0.04, ErrorRate: 0.12, BytesMedian: 14_000,
                Errors: [new(422, "HTTP 422 Payment declined", 3), new(500, "HTTP 500 Internal Server Error on POST /payments", 2), new(null, "Payment provider timed out after 10 s", 1)],
                FailAtCall: 3, ForceFailOn: 3));
        Add(Entry("user-journey", "Browse categories", 60, "",
                urls("https://shop.example.com/", "https://shop.example.com/categories/12", "https://shop.example.com/categories/7", "https://shop.example.com/categories/31",
                     "https://shop.example.com/categories/31?page=2", "https://shop.example.com/products/918", "https://shop.example.com/products/204")),
            new("Open the home page, look through 3 categories with pagination, open 2 products.",
                [new("GET /", 200), .. Repeat("GET /categories/{id}", 3, 260), new("GET /categories/{id}?page=2", 280), new("GET /products/{id} · 1", 240), new("GET /products/{id} · 2", 240)],
                ThinkMs: 900, MinCalls: 5, TailChance: 0.03, ErrorRate: 0.01, BytesMedian: 120_000,
                Errors: [new(503, "HTTP 503 Service Unavailable on GET /categories/{id}", 1)], FailAtCall: 2));
        Add(Entry("http-custom", "Sign up & first order", 8, "https://shop.example.com/api/accounts"),
            new("Create an account, log in, get recommendations and place a first order.",
                [new("POST /accounts", 520), new("POST /login", 300), new("GET /recommendations", 700, 0.5), new("POST /cart/items", 320), new("POST /checkout", 1100, 0.45), new("GET /orders/{id}/confirmation", 280)],
                ThinkMs: 1800, TailChance: 0.05, ErrorRate: 0.06, BytesMedian: 36_000,
                Errors: [new(500, "HTTP 500 Internal Server Error on POST /checkout", 2), new(422, "HTTP 422 Payment declined", 1)], FailAtCall: 4));
        Add(Entry("user-journey", "Order history & return", 6, "",
                urls("https://shop.example.com/login", "https://shop.example.com/orders", "https://shop.example.com/orders/88231", "https://shop.example.com/orders/88231/items")),
            new("Log in, look up an old order and return one item.",
                [new("POST /login", 300), new("GET /orders", 450), new("GET /orders/{id}", 380), new("GET /orders/{id}/items", 300), new("POST /returns", 850, 0.5), new("POST /returns/{id}/label", 500)],
                ThinkMs: 2000, TailChance: 0.04, ErrorRate: 0.1, BytesMedian: 22_000,
                Errors: [new(502, "HTTP 502 Bad Gateway on POST /returns", 1), new(409, "HTTP 409 Item is not returnable", 1)], FailAtCall: 4));
    }

    private FakePreset MakeSmoke()
    {
        var plan = new LoadPlan { DurationSeconds = 60, MaxConcurrency = 10, TimeoutSeconds = 60 };
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
