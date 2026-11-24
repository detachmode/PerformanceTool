using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Beesharp.Services.Scenarios;
using Microsoft.Extensions.Logging;

namespace HostExample;

public sealed record ShopCheckoutParams(
    [property: Description("Search query used in the Search step")]
    string Query = "honey",
    [property: Description("Simulated latency (ms) per step")]
    [property: Range(5, 5000)]
    int DelayMs = 40
);

public sealed record Search(string Query);
public sealed record AddToCart(string ProductId);
public sealed record Pay(string ProductId);

public union ShopStep(Search, AddToCart, Pay);

/// <summary>
/// Example compiled C# 15 union step-machine scenario injecting <see cref="DemoTokenCache"/> via standard DI.
/// Set a breakpoint in <see cref="RunStepAsync"/> and press F5 in your IDE!
/// </summary>
public sealed class ShopCheckoutScenario(DemoTokenCache tokenCache) : Scenario<ShopCheckoutParams, ShopStep>
{
    public override string Id => "shop-checkout";
    public override string Name => "Shop Checkout";
    public override string Description => "Compiled scenario using a shared DI token cache and C# 15 union steps.";

    protected override ShopStep StartStep(ShopCheckoutParams parameters) => new Search(parameters.Query);

    protected override async Task<ShopStep?> RunStepAsync(
        ShopStep step,
        ShopCheckoutParams p,
        ScenarioContext ctx,
        CancellationToken ct)
    {
        var token = await tokenCache.GetTokenAsync(ctx, ct);

        return step switch
        {
            Search s => await SearchAsync(s, token, p, ctx, ct),
            AddToCart a => await AddToCartAsync(a, token, p, ctx, ct),
            Pay pay => await PayAsync(pay, token, p, ctx, ct),
        };
    }

    private static async Task<ShopStep?> SearchAsync(Search s, string token, ShopCheckoutParams p, ScenarioContext ctx, CancellationToken ct)
    {
        var productId = $"SKU-{Random.Shared.Next(100, 999)}";
        await ctx.MeasureCallAsync($"GET /api/search?q={s.Query}", async () =>
        {
            await Task.Delay(p.DelayMs, ct);
            ctx.Logger.LogInformation("Search '{Query}' ({Token}) -> picked {ProductId}", s.Query, token, productId);
        });
        return new AddToCart(productId);
    }

    private static async Task<ShopStep?> AddToCartAsync(AddToCart a, string token, ShopCheckoutParams p, ScenarioContext ctx, CancellationToken ct)
    {
        await ctx.MeasureCallAsync($"POST /api/cart/{a.ProductId}", async () =>
        {
            await Task.Delay(p.DelayMs, ct);
            ctx.Logger.LogInformation("Added {ProductId} to cart ({Token})", a.ProductId, token);
        });
        return new Pay(a.ProductId);
    }

    private static async Task<ShopStep?> PayAsync(Pay pay, string token, ShopCheckoutParams p, ScenarioContext ctx, CancellationToken ct)
    {
        await ctx.MeasureCallAsync($"POST /api/pay/{pay.ProductId}", async () =>
        {
            await Task.Delay(p.DelayMs, ct);
            ctx.Logger.LogInformation("Paid for {ProductId} ({Token})", pay.ProductId, token);
        });
        return null;
    }
}
