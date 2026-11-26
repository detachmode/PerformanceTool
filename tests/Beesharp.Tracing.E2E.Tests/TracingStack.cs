using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Beesharp.Contracts;
using Beesharp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Tracing.E2E.Tests;

/// <summary>
/// The tracing example as processes (brain 0063): Aspire starts Beesharp, shop-api and payment-api, all three export to
/// <see cref="Receiver"/>, and the "Shop journey" preset runs for a few seconds. Started once for all tests of the class.
/// </summary>
public sealed class TracingStack : IAsyncLifetime
{
    /// <summary>The scenario's steps in the order a bee visits them (spans are named after them, see <c>shop-journey.cs</c>).</summary>
    public static readonly string[] StepSpanNames = ["Log in", "Search", "Open product", "Open product #2", "Open product #3", "Pay"];
    public static readonly string[] Services = ["beesharp", "payment-api", "shop-api"];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SpanTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<string> logTail = new();
    private readonly ConcurrentDictionary<string, string> resourceStates = new();
    private readonly CancellationTokenSource stopLogs = new();
    private DistributedApplication? app;
    private HttpClient? beesharp;
    private string dataDirectory = "";

    public OtlpReceiver Receiver { get; private set; } = null!;
    public string RunId { get; private set; } = "";

    /// <summary>The finished bees of the run with their stored calls (trace and span IDs), read through Beesharp's REST API.</summary>
    public IReadOnlyList<StoredBeeDetail> Bees { get; private set; } = [];

    /// <summary>Per bee sequence: what is still wrong with its exported trace after waiting (empty = complete and correct).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> TraceProblems { get; private set; } = new Dictionary<int, IReadOnlyList<string>>();

    public async Task InitializeAsync()
    {
        Receiver = await OtlpReceiver.StartAsync();
        dataDirectory = Path.Combine(Path.GetTempPath(), "beesharp-tracing-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Beesharp_Tracing_E2E_AppHost>(
                [$"--E2E:OtlpEndpoint={Receiver.Endpoint}", $"--E2E:DataDirectory={dataDirectory}"],
                (options, _) => options.DisableDashboard = true); // nobody needs the dashboard, and it would also grab OTLP ports
            app = await builder.BuildAsync();
            await app.StartAsync();
            CaptureResourceLogs();

            foreach (var name in new[] { "payment-api", "shop-api", "beesharp" })
                await app.ResourceNotifications.WaitForResourceAsync(name, KnownResourceStates.Running).WaitAsync(StartTimeout);
            beesharp = app.CreateHttpClient("beesharp");
            await WaitUntilAsync("Beesharp answers on /api/scenarios", StartTimeout, async () =>
                (await TryGetAsync(beesharp, "/api/scenarios"))?.IsSuccessStatusCode == true);
            var shopUrl = app.GetEndpoint("shop-api", "http");
            await WaitUntilAsync("shop-api answers on /login", StartTimeout, async () =>
            {
                using var http = new HttpClient();
                return (await TryGetAsync(http, new Uri(shopUrl, "/login").ToString()))?.IsSuccessStatusCode == true;
            });

            RunId = await RunShopJourneyAsync(shopUrl);
            Bees = await ReadBeesAsync();
            TraceProblems = await WaitForExportedTracesAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The tracing stack did not come up or the run failed: {ex.Message}\nLast lines of the services' output:\n{string.Join('\n', logTail)}", ex);
        }
    }

    // Loads the example's preset through the REST API, points it at the shop-api Aspire started, makes it short and runs it.
    private async Task<string> RunShopJourneyAsync(Uri shopUrl)
    {
        using var load = await beesharp!.PostAsync("/api/presets/Shop%20journey/load", null);
        Assert.True(load.IsSuccessStatusCode, $"Loading the preset 'Shop journey' failed: {(int)load.StatusCode} {await load.Content.ReadAsStringAsync()}. Is shop-journey.json in the presets folder?");
        var plan = (await load.Content.ReadFromJsonAsync<PlanWorkspaceState>(Web))!.Plan;
        var entry = Assert.Single(plan.Scenarios);
        entry.Parameters["baseUrl"] = shopUrl.ToString().TrimEnd('/');
        entry.RatePerMinute = 120;
        plan.DurationSeconds = 8;

        using var start = await beesharp.PostAsJsonAsync("/api/runs/start", new StartRunRequest(plan), Web);
        Assert.True(start.IsSuccessStatusCode, $"Starting the run failed: {(int)start.StatusCode} {await start.Content.ReadAsStringAsync()}");
        var runId = (await start.Content.ReadFromJsonAsync<LiveRunStatusDto>(Web))!.RunId;

        string state = "";
        await WaitUntilAsync("the run finishes", RunTimeout, async () =>
        {
            var current = await beesharp.GetFromJsonAsync<LiveRunStatusDto>("/api/runs/current?maxBees=1", Web);
            state = current?.State ?? "";
            return current is { RunId: var id } && id == runId && state is "Finished" or "Failed" or "Stopped";
        });
        Assert.Equal("Finished", state);
        return runId;
    }

    private async Task<IReadOnlyList<StoredBeeDetail>> ReadBeesAsync()
    {
        var page = await beesharp!.GetFromJsonAsync<JsonElement>($"/api/runs/{RunId}/bees?limit=500", Web);
        var sequences = page.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("sequence").GetInt32()).Order().ToList();
        var bees = new List<StoredBeeDetail>();
        foreach (var sequence in sequences)
            bees.Add((await beesharp!.GetFromJsonAsync<StoredBeeDetail>($"/api/runs/{RunId}/bees/{sequence}", Web))!);
        return bees;
    }

    // Spans arrive in batches (a few seconds after the call), so wait until every bee's trace is complete, but never longer than SpanTimeout.
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> WaitForExportedTracesAsync()
    {
        var deadline = DateTime.UtcNow + SpanTimeout;
        while (true)
        {
            var spans = Receiver.Snapshot();
            var problems = Bees.ToDictionary(b => b.Sequence, b => (IReadOnlyList<string>)TraceChecks.Check(b, spans));
            if (problems.Values.All(p => p.Count == 0) || DateTime.UtcNow > deadline) return problems;
            await Task.Delay(500);
        }
    }

    private void CaptureResourceLogs()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in app!.ResourceNotifications.WatchAsync(stopLogs.Token))
                {
                    var state = change.Snapshot.State?.Text ?? "";
                    resourceStates[change.Resource.Name] = state;
                    logTail.Enqueue($"[aspire] {change.Resource.Name}: {state} {change.Snapshot.ExitCode}");
                    while (logTail.Count > 60) logTail.TryDequeue(out _);
                }
            }
            catch (OperationCanceledException) { }
        });
        var logs = app!.Services.GetRequiredService<ResourceLoggerService>();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        foreach (var resource in model.Resources.Where(r => r.Name is "beesharp" or "shop-api" or "payment-api"))
        {
            var name = resource.Name;
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var batch in logs.WatchAsync(resource).WithCancellation(stopLogs.Token))
                        foreach (var line in batch)
                        {
                            logTail.Enqueue($"[{name}] {line.Content}");
                            while (logTail.Count > 60) logTail.TryDequeue(out _);
                        }
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    private static async Task<HttpResponseMessage?> TryGetAsync(HttpClient http, string url)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return await http.GetAsync(url, cts.Token); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return null; }
    }

    private async Task WaitUntilAsync(string what, TimeSpan timeout, Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await done())
        {
            // A process that died will never answer: say so right away instead of waiting for the timeout.
            var dead = resourceStates.Where(r => r.Value is "Exited" or "Finished" or "FailedToStart").Select(r => $"{r.Key} ({r.Value})").ToList();
            if (dead.Count > 0) throw new InvalidOperationException($"While waiting until {what}: {string.Join(", ", dead)} is not running any more.");
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting until {what}.");
            await Task.Delay(300);
        }
    }

    public async Task DisposeAsync()
    {
        await stopLogs.CancelAsync();
        beesharp?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await Receiver.DisposeAsync();
        try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
