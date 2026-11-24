#:package Beesharp.Core@0.2.4
// Example C# 15 union step-machine scenario using IAsyncEnumerable + yield return (brain 0056).
// Runs out of the box without an external server: uses simulated calls + ILogger so you can test the
// chronological stepper, captured step data, dynamic repeats, retries, and per-step failure filtering.
// Run locally with `dotnet run examples/checkout-steps.cs` (or F5 in your IDE), or copy/mount into scripts/.

var scenario = Scenario.DefineSteps<CheckoutStepsParams, CheckoutStep>(
    "Checkout (Union Steps)",
    start: _ => new LogIn(),
    run: (step, p, ctx) => step switch
    {
        LogIn => LogInAsync(p, ctx),
        Search => SearchAsync(p, ctx),
        OpenProduct o => OpenProductAsync(o, p, ctx),
        AddToCart a => AddToCartAsync(a, p, ctx),
        Pay pay => PayAsync(pay, p, ctx),
    })
    .WithDescription("C# 15 union step-machine demo with yield return: Log in -> Search -> Open product (randomly yields Add to cart) -> Pay (with retry).");

await scenario.DebugRunAsync(args);

Task RandomDelayAsync(CheckoutStepsParams p, ScenarioContext ctx)
{
    var min = Math.Max(0, p.MinDelayMs);
    var max = Math.Max(min, p.MaxDelayMs);
    return Task.Delay(Random.Shared.Next(min, max + 1), ctx.CancellationToken);
}

async IAsyncEnumerable<CheckoutStep> LogInAsync(CheckoutStepsParams p, ScenarioContext ctx)
{
    var token = $"tok-{Random.Shared.Next(1000, 9999)}";
    await ctx.MeasureCallAsync("POST /api/login", async () =>
    {
        await RandomDelayAsync(p, ctx);
        ctx.State["Token"] = token;
        ctx.Logger.LogInformation("Logged in with token {Token}", token);
    });
    yield return new Search();
}

async IAsyncEnumerable<CheckoutStep> SearchAsync(CheckoutStepsParams p, ScenarioContext ctx)
{
    var token = ctx.State.Get<string>("Token") ?? "tok-missing";
    var cartId = $"cart-{Random.Shared.Next(100, 999)}";
    var ids = Enumerable.Range(1, Math.Max(p.OpenCount, 5)).Select(i => $"SKU-{i:000}").ToArray();

    await ctx.MeasureCallAsync($"GET /api/search?q={p.Query}", async () =>
    {
        await RandomDelayAsync(p, ctx);
        ctx.State["CartId"] = cartId;
        ctx.Logger.LogInformation("Search for '{Query}' ({Token}) returned {Count} products, created {CartId}", p.Query, token, ids.Length, cartId);
    });

    // Yield one OpenProduct step per product to inspect, then Pay at the end:
    foreach (var id in ids.Take(p.OpenCount))
        yield return new OpenProduct(id);

    yield return new Pay();
}

async IAsyncEnumerable<CheckoutStep> OpenProductAsync(OpenProduct o, CheckoutStepsParams p, ScenarioContext ctx)
{
    await ctx.MeasureCallAsync($"GET /api/products/{o.ProductId}", async () =>
    {
        await RandomDelayAsync(p, ctx);
        if (Random.Shared.Next(100) < p.OpenFailPercent)
            throw new InvalidOperationException($"Product {o.ProductId} returned HTTP 404 Not Found");
        ctx.Logger.LogInformation("Opened product {ProductId}", o.ProductId);
    });

    // Randomly add this opened product to the cart right away (depth-first before the next product):
    if (Random.Shared.Next(100) < p.AddToCartPercent)
        yield return new AddToCart(o.ProductId);
}

async IAsyncEnumerable<CheckoutStep> AddToCartAsync(AddToCart a, CheckoutStepsParams p, ScenarioContext ctx)
{
    var cartId = ctx.State.Get<string>("CartId") ?? "cart-missing";
    await ctx.MeasureCallAsync($"POST /api/cart/{cartId}/items/{a.ProductId}", async () =>
    {
        await RandomDelayAsync(p, ctx);
        ctx.Logger.LogInformation("Added {ProductId} to {CartId}", a.ProductId, cartId);
    });
    yield break;
}

async IAsyncEnumerable<CheckoutStep> PayAsync(Pay pay, CheckoutStepsParams p, ScenarioContext ctx)
{
    var cartId = ctx.State.Get<string>("CartId") ?? "cart-missing";
    var token = ctx.State.Get<string>("Token") ?? "tok-missing";
    var retry = false;

    await ctx.MeasureCallAsync($"POST /api/pay/{cartId}", async () =>
    {
        await RandomDelayAsync(p, ctx);
        if (pay.Attempt == 1 && Random.Shared.Next(100) < p.PayRetryPercent)
        {
            ctx.Logger.LogWarning("Payment gateway busy on attempt #1 for {CartId} ({Token}), retrying...", cartId, token);
            retry = true;
            return;
        }
        if (Random.Shared.Next(100) < p.PayFailPercent)
            throw new InvalidOperationException($"Payment declined on attempt #{pay.Attempt} for {cartId}");
        ctx.Logger.LogInformation("Payment succeeded for {CartId} on attempt #{Attempt}", cartId, pay.Attempt);
    });

    if (retry)
        yield return pay with { Attempt = pay.Attempt + 1 };
}

// --- Parameters & C# 15 union steps ---
record CheckoutStepsParams(
    [property: Description("Search query logged on the Search step")]
    string Query = "honey",
    [property: Description("How many products each bee opens")]
    [property: Range(1, 10)]
    int OpenCount = 4,
    [property: Description("Chance (%) that an opened product is added to the cart")]
    [property: Range(0, 100)]
    int AddToCartPercent = 50,
    [property: Description("Minimum random delay (ms) per step")]
    [property: Range(10, 5000)]
    int MinDelayMs = 200,
    [property: Description("Maximum random delay (ms) per step")]
    [property: Range(10, 10000)]
    int MaxDelayMs = 1200,
    [property: Description("Chance (%) that opening a product fails (404 / stock error)")]
    [property: Range(0, 100)]
    int OpenFailPercent = 10,
    [property: Description("Chance (%) that payment retries once before succeeding or failing")]
    [property: Range(0, 100)]
    int PayRetryPercent = 35,
    [property: Description("Chance (%) that payment ultimately fails")]
    [property: Range(0, 100)]
    int PayFailPercent = 15
);

// Each step record carries only step-specific data (Token and CartId live in ctx.State).
// PascalCase names auto-format ("LogIn" -> "Log in"); [Display] overrides when needed.
record LogIn;
record Search;
record OpenProduct(string ProductId);
record AddToCart(string ProductId);
record Pay(int Attempt = 1);

union CheckoutStep(LogIn, Search, OpenProduct, AddToCart, Pay);
