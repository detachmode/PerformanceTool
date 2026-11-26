// Scenario for the tracing example: log in, search, open a few products, pay.
// Each bee is one trace; shop-api and payment-api spans appear below its calls in Jaeger or Kibana.
using System.Diagnostics;

public record ShopJourneyParams(
    [property: Description("Base URL of shop-api")]
    string BaseUrl = "http://shop-api:8080",
    [property: Description("How many products each bee opens")]
    [property: Range(1, 10)]
    int Products = 3);

public record LogIn;
public record Search;
public record OpenProduct(int Index);
public record Pay;
public union ShopStep(LogIn, Search, OpenProduct, Pay);

Scenario.DefineSteps<ShopJourneyParams, ShopStep>(
    "Shop journey",
    start: _ => new LogIn(),
    run: async (step, p, ctx) => step switch
    {
        LogIn => await DoLogIn(ctx, p),
        Search => await DoSearch(ctx, p),
        OpenProduct o => await DoOpenProduct(ctx, p, o),
        Pay => await DoPay(ctx, p),
    });

async Task<ShopStep?> DoLogIn(ScenarioContext ctx, ShopJourneyParams p)
{
    ctx.Logger.LogInformation("Logging in to {BaseUrl}", p.BaseUrl);
    using var res = await ctx.Http.GetAsync(p.BaseUrl + "/login", ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Login failed with HTTP {StatusCode}", (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Login succeeded");
    return new Search();
}

async Task<ShopStep?> DoSearch(ScenarioContext ctx, ShopJourneyParams p)
{
    const string query = "honey";
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/search?q={query}", ctx.CancellationToken);
    sw.Stop();
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Search for {Query} failed with HTTP {StatusCode}", query, (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    if (sw.ElapsedMilliseconds >= 350)
        ctx.Logger.LogWarning("Slow search for {Query} took {ElapsedMs} ms", query, sw.ElapsedMilliseconds);
    else
        ctx.Logger.LogInformation("Searched {Query} in {ElapsedMs} ms", query, sw.ElapsedMilliseconds);
    return new OpenProduct(1);
}

async Task<ShopStep?> DoOpenProduct(ScenarioContext ctx, ShopJourneyParams p, OpenProduct o)
{
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/products/{o.Index}", ctx.CancellationToken);
    sw.Stop();
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Loading product {ProductId} failed with HTTP {StatusCode}", o.Index, (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    if (sw.ElapsedMilliseconds >= 350)
        ctx.Logger.LogWarning("Slow product lookup for {ProductId} ({Index}/{Total}) took {ElapsedMs} ms", o.Index, o.Index, p.Products, sw.ElapsedMilliseconds);
    else
        ctx.Logger.LogInformation("Opened product {ProductId} ({Index}/{Total}) in {ElapsedMs} ms", o.Index, o.Index, p.Products, sw.ElapsedMilliseconds);
    return o.Index < p.Products ? o with { Index = o.Index + 1 } : new Pay();
}

async Task<ShopStep?> DoPay(ScenarioContext ctx, ShopJourneyParams p)
{
    ctx.Logger.LogInformation("Submitting payment for order");
    using var res = await ctx.Http.PostAsync(p.BaseUrl + "/pay", null, ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Payment failed with HTTP {StatusCode}", (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Payment completed");
    return null;
}
