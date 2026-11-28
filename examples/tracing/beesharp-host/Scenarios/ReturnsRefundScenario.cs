using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Beesharp.Services.Scenarios;
using Microsoft.Extensions.Logging;

namespace Beesharp.Tracing.Host.Scenarios;

public sealed record ReturnsRefundParams(
    [property: Description("Base URL of shop-api")]
    string BaseUrl = "http://localhost:5292",
    [property: Description("Product ID being returned")]
    [property: Range(1, 20)]
    int ProductId = 2);

public sealed record ReturnsAuthenticate;
public sealed record LookupProduct;
public sealed record SubmitReturn;

public union ReturnsStep(ReturnsAuthenticate, LookupProduct, SubmitReturn);

/// <summary>Compiled scenario for processing a customer return and downstream refund.</summary>
public sealed class ReturnsRefundScenario : Scenario<ReturnsRefundParams, ReturnsStep>
{
    public override string Id => "returns-refund";
    public override string Name => "Returns & refund";
    public override string Description => "Authenticates, verifies a product, and submits a return and refund.";

    protected override ReturnsStep StartStep(ReturnsRefundParams parameters) => new ReturnsAuthenticate();

    protected override Task<ReturnsStep?> RunStepAsync(
        ReturnsStep step,
        ReturnsRefundParams p,
        ScenarioContext ctx,
        CancellationToken ct) => step switch
        {
            ReturnsAuthenticate => DoAuthenticate(ctx, p, ct),
            LookupProduct => DoLookupProduct(ctx, p, ct),
            SubmitReturn => DoSubmitReturn(ctx, p, ct),
        };

    private static async Task<ReturnsStep?> DoAuthenticate(ScenarioContext ctx, ReturnsRefundParams p, CancellationToken ct)
    {
        using var res = await ctx.Http.GetAsync(p.BaseUrl + "/login", ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Returns authentication failed with HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Authenticated customer for return request");
        return new LookupProduct();
    }

    private static async Task<ReturnsStep?> DoLookupProduct(ScenarioContext ctx, ReturnsRefundParams p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/products/{p.ProductId}", ct);
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

    private static async Task<ReturnsStep?> DoSubmitReturn(ScenarioContext ctx, ReturnsRefundParams p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.PostAsync(p.BaseUrl + "/returns", null, ct);
        sw.Stop();
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Return refund for product {ProductId} failed with HTTP {StatusCode}", p.ProductId, (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Return and refund completed for product {ProductId} in {ElapsedMs} ms", p.ProductId, sw.ElapsedMilliseconds);
        return null;
    }
}
