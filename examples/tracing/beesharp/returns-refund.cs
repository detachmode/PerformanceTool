// Scenario for processing a customer return and downstream refund.
using System.Diagnostics;

public record ReturnsRefundParams(
    [property: Description("Base URL of shop-api")]
    string BaseUrl = "http://shop-api:8080",
    [property: Description("Product ID being returned")]
    [property: Range(1, 20)]
    int ProductId = 2);

public record Authenticate;
public record LookupProduct;
public record SubmitReturn;
public union ReturnsStep(Authenticate, LookupProduct, SubmitReturn);

Scenario.DefineSteps<ReturnsRefundParams, ReturnsStep>(
    "Returns & refund",
    start: _ => new Authenticate(),
    run: async (step, p, ctx) => step switch
    {
        Authenticate => await DoAuthenticate(ctx, p),
        LookupProduct => await DoLookupProduct(ctx, p),
        SubmitReturn => await DoSubmitReturn(ctx, p),
    });

async Task<ReturnsStep?> DoAuthenticate(ScenarioContext ctx, ReturnsRefundParams p)
{
    using var res = await ctx.Http.GetAsync(p.BaseUrl + "/login", ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Returns authentication failed with HTTP {StatusCode}", (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Authenticated customer for return request");
    return new LookupProduct();
}

async Task<ReturnsStep?> DoLookupProduct(ScenarioContext ctx, ReturnsRefundParams p)
{
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/products/{p.ProductId}", ctx.CancellationToken);
    sw.Stop();
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Lookup of product {ProductId} failed with HTTP {StatusCode}", p.ProductId, (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    if (sw.ElapsedMilliseconds >= 350)
        ctx.Logger.LogWarning("Slow lookup for product {ProductId} took {ElapsedMs} ms", p.ProductId, sw.ElapsedMilliseconds);
    else
        ctx.Logger.LogInformation("Verified product {ProductId} for return in {ElapsedMs} ms", p.ProductId, sw.ElapsedMilliseconds);
    return new SubmitReturn();
}

async Task<ReturnsStep?> DoSubmitReturn(ScenarioContext ctx, ReturnsRefundParams p)
{
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.PostAsync(p.BaseUrl + "/returns", null, ctx.CancellationToken);
    sw.Stop();
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Return refund for product {ProductId} failed with HTTP {StatusCode}", p.ProductId, (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Return and refund completed for product {ProductId} in {ElapsedMs} ms", p.ProductId, sw.ElapsedMilliseconds);
    return null;
}
