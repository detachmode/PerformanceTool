public record OrderFlowParams(
    [property: Description("Tenant or store name logged on each step")]
    string StoreName = "DemoShop",
    [property: Description("Number of items checked in inventory")]
    [property: Range(1, 5)]
    int ItemCount = 3,
    [property: Range(1, 500)]
    int Delay = 200,
    [property: Description("Chance (%) of a retry attempt on payment")]
    [property: Range(0, 100)]
    int RetryChancePercent = 40,
    [property: Description("Chance (%) that payment ultimately fails")]
    [property: Range(0, 100)]
    int FailurePercent = 10
);

Scenario.Define<OrderFlowParams>("Multi-Step Order Flow (ILogger)", async (p, ctx) =>
{
    var log = ctx.Logger;
    await Task.Delay(p.Delay);
    var token = ctx.State.TryGetValue("token", out var t) ? t?.ToString() : "no-token";
    log.LogInformation("Starting order flow for {Store} with {Items} items (token={Token})", p.StoreName, p.ItemCount, token);

    await ctx.MeasureCallAsync("1. Search catalog", async () =>
    {
        var ms = Random.Shared.Next(30, 90);
        await Task.Delay(ms, ctx.CancellationToken);
        log.LogInformation("Catalog search returned 24 products in {Ms} ms", ms);
    });

    for (var i = 1; i <= p.ItemCount; i++)
    {
        var idx = i;
        await ctx.MeasureCallAsync($"2.{idx} Check inventory #{idx}", async () =>
        {
            var ms = Random.Shared.Next(20, 70);
            await Task.Delay(ms, ctx.CancellationToken);
            log.LogInformation("Inventory check for item #{Item} OK ({Ms} ms)", idx, ms);
        });
    }

    await ctx.MeasureCallAsync("3. Reserve cart items", async () =>
    {
        var ms = Random.Shared.Next(40, 110);
        await Task.Delay(ms, ctx.CancellationToken);
        log.LogInformation("Reserved {Items} items in {Ms} ms", p.ItemCount, ms);
    });

    await ctx.MeasureCallAsync("4. Authorize payment", async () =>
    {
        var attempt = 1;
        while (true)
        {
            var ms = Random.Shared.Next(50, 160);
            await Task.Delay(ms, ctx.CancellationToken);
            if (attempt == 1 && Random.Shared.Next(100) < p.RetryChancePercent)
            {
                log.LogWarning("Payment gateway attempt #{Attempt} timed out after {Ms} ms, retrying...", attempt, ms);
                attempt++;
                continue;
            }
            if (Random.Shared.Next(100) < p.FailurePercent)
            {
                log.LogError("Payment authorization declined on attempt #{Attempt} after {Ms} ms", attempt, ms);
                throw new InvalidOperationException($"Payment declined on attempt #{attempt}");
            }
            log.LogInformation("Payment authorized on attempt #{Attempt} in {Ms} ms", attempt, ms);
            break;
        }
    });

    await ctx.MeasureCallAsync("5. Create order", async () =>
    {
        var ms = Random.Shared.Next(35, 100);
        await Task.Delay(ms, ctx.CancellationToken);
        log.LogInformation("Order confirmed for {Store} in {Ms} ms", p.StoreName, ms);
    });
})
.WithDescription("Simulated multi-call e-commerce order journey with ILogger statements, delays, and retry attempts (no real HTTP calls).")
.OnStartup(async (p, ctx) =>
{
    ctx.State["token"] = $"tok-{Random.Shared.Next(1000, 9999)}";
    ctx.Logger.LogInformation("Startup: fetched initial auth token {Token} for {Store}", ctx.State["token"], p.StoreName);
    await Task.CompletedTask;
})
.OnTeardown(async (p, ctx) =>
{
    ctx.Logger.LogInformation("Teardown: released session for {Store}", p.StoreName);
    await Task.CompletedTask;
});