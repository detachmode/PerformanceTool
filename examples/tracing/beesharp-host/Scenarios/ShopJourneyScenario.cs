using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Beesharp.Services.Scenarios;
using Microsoft.Extensions.Logging;

namespace Beesharp.Tracing.Host.Scenarios;

public sealed record ShopJourneyParams(
    [property: Description("Base URL of shop-api")]
    string BaseUrl = "http://localhost:5292",
    [property: Description("How many products each bee opens")]
    [property: Range(1, 10)]
    int Products = 3);

public sealed record LogIn;
public sealed record Search;
public sealed record OpenProduct(int Index);
public sealed record Pay;

public union ShopStep(LogIn, Search, OpenProduct, Pay);

/// <summary>Compiled scenario for the tracing example: log in, search, open products, and pay.</summary>
public sealed class ShopJourneyScenario : Scenario<ShopJourneyParams, ShopStep>
{
    public override string Id => "shop-journey";
    public override string Name => "Shop journey";
    public override string Description => "Logs in, searches for honey, opens products, and submits payment.";

    protected override ShopStep StartStep(ShopJourneyParams parameters) => new LogIn();

    protected override Task<ShopStep?> RunStepAsync(
        ShopStep step,
        ShopJourneyParams p,
        ScenarioContext ctx,
        CancellationToken ct) => step switch
        {
            LogIn => DoLogIn(ctx, p, ct),
            Search => DoSearch(ctx, p, ct),
            OpenProduct o => DoOpenProduct(ctx, p, o, ct),
            Pay => DoPay(ctx, p, ct),
        };

    private static async Task<ShopStep?> DoLogIn(ScenarioContext ctx, ShopJourneyParams p, CancellationToken ct)
    {
        ctx.Logger.LogInformation("Logging in to {BaseUrl}", p.BaseUrl);
        using var res = await ctx.Http.GetAsync(p.BaseUrl + "/login", ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Login failed with HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Login succeeded");
        return new Search();
    }

    private static async Task<ShopStep?> DoSearch(ScenarioContext ctx, ShopJourneyParams p, CancellationToken ct)
    {
        const string query = "honey";
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/search?q={query}", ct);
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

    private static async Task<ShopStep?> DoOpenProduct(ScenarioContext ctx, ShopJourneyParams p, OpenProduct o, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/products/{o.Index}", ct);
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

    private static async Task<ShopStep?> DoPay(ScenarioContext ctx, ShopJourneyParams p, CancellationToken ct)
    {
        ctx.Logger.LogInformation("Submitting payment for order");
        using var res = await ctx.Http.PostAsync(p.BaseUrl + "/pay", null, ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Payment failed with HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Payment completed");
        return null;
    }
}
