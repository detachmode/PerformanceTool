public record SimulatedWorkParams(
    [property: Description("Name included in log output")]
    string WorkName = "Simulated checkout",
    [property: Range(10, 5000)]
    int MinDelayMs = 80,
    [property: Range(10, 5000)]
    int MaxDelayMs = 250,
    [property: Range(0, 100)]
    int FailurePercent = 10
);

Scenario.Define<SimulatedWorkParams>("Simulated Work (Delay + Logs)", async (p, ctx) =>
{
    var session = ctx.State.TryGetValue("session", out var s) ? s?.ToString() : "none";
    ctx.Log($"[{p.WorkName}] Step 1: starting (session={session})");

    var low = Math.Min(p.MinDelayMs, p.MaxDelayMs);
    var high = Math.Max(p.MinDelayMs, p.MaxDelayMs);
    var step1 = Random.Shared.Next(low, high + 1);
    await Task.Delay(step1, ctx.CancellationToken);
    ctx.Log($"[{p.WorkName}] Step 1 finished in {step1} ms");

    var step2 = Random.Shared.Next(low / 2, Math.Max(low / 2 + 1, high / 2));
    await Task.Delay(step2, ctx.CancellationToken);
    ctx.Log($"[{p.WorkName}] Step 2 finished in {step2} ms");

    if (p.FailurePercent > 0 && Random.Shared.Next(100) < p.FailurePercent)
    {
        ctx.Log($"[{p.WorkName}] Simulated failure triggered!");
        throw new InvalidOperationException($"{p.WorkName} simulated failure");
    }

    ctx.Log($"[{p.WorkName}] Completed cleanly");
})
.WithDescription("Fake workload using Task.Delay and log statements to test live runs without hitting an external server.")
.OnStartup(async (p, ctx) =>
{
    ctx.State["session"] = $"sess-{Random.Shared.Next(1000, 9999)}";
    ctx.Log($"Startup: initialized {ctx.State["session"]}");
    await Task.CompletedTask;
})
.OnTeardown(async (p, ctx) =>
{
    ctx.Log("Teardown: cleaned up session");
    await Task.CompletedTask;
});