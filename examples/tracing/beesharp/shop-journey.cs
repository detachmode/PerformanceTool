// Scenario for the tracing example: log in, search, open a few products, pay.
// Each bee is one trace; shop-api and payment-api spans appear below its calls in Jaeger or Kibana.
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
        LogIn => await Get(ctx, p, "/login", new Search()),
        Search => await Get(ctx, p, "/search?q=honey", new OpenProduct(1)),
        OpenProduct o => await Get(ctx, p, $"/products/{o.Index}", o.Index < p.Products ? o with { Index = o.Index + 1 } : new Pay()),
        Pay => await DoPay(ctx, p),
    });

async Task<ShopStep?> Get(ScenarioContext ctx, ShopJourneyParams p, string path, ShopStep next)
{
    using var res = await ctx.Http.GetAsync(p.BaseUrl + path, ctx.CancellationToken);
    res.EnsureSuccessStatusCode();
    return next;
}

async Task<ShopStep?> DoPay(ScenarioContext ctx, ShopJourneyParams p)
{
    using var res = await ctx.Http.PostAsync(p.BaseUrl + "/pay", null, ctx.CancellationToken);
    res.EnsureSuccessStatusCode();
    return null;
}
