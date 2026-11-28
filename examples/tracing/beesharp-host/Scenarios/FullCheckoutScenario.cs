using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Beesharp.Services.Scenarios;
using Microsoft.Extensions.Logging;

namespace Beesharp.Tracing.Host.Scenarios;

public sealed record FullCheckoutParams(
    [property: Description("Base URL of shop-api")]
    string BaseUrl = "http://localhost:5292",
    [property: Description("How many items to add to the cart")]
    [property: Range(1, 10)]
    int Items = 2,
    [property: Description("Promo coupon code to apply")]
    string CouponCode = "HONEY10");

public sealed record CheckoutAuthenticate;
public sealed record AddToCart(int Index);
public sealed record ViewCart;
public sealed record ApplyCoupon;
public sealed record ReserveStock;
public sealed record CompletePayment;

public union CheckoutStep(CheckoutAuthenticate, AddToCart, ViewCart, ApplyCoupon, ReserveStock, CompletePayment);

/// <summary>Compiled scenario for a full checkout flow: login, cart items, coupon, stock reservation, and payment.</summary>
public sealed class FullCheckoutScenario : Scenario<FullCheckoutParams, CheckoutStep>
{
    public override string Id => "full-checkout";
    public override string Name => "Full checkout";
    public override string Description => "Authenticates, adds items to cart, applies a coupon, reserves stock, and completes payment.";

    protected override CheckoutStep StartStep(FullCheckoutParams parameters) => new CheckoutAuthenticate();

    protected override Task<CheckoutStep?> RunStepAsync(
        CheckoutStep step,
        FullCheckoutParams p,
        ScenarioContext ctx,
        CancellationToken ct) => step switch
        {
            CheckoutAuthenticate => DoAuthenticate(ctx, p, ct),
            AddToCart a => DoAddToCart(ctx, p, a, ct),
            ViewCart => DoViewCart(ctx, p, ct),
            ApplyCoupon => DoApplyCoupon(ctx, p, ct),
            ReserveStock => DoReserveStock(ctx, p, ct),
            CompletePayment => DoCompletePayment(ctx, p, ct),
        };

    private static async Task<CheckoutStep?> DoAuthenticate(ScenarioContext ctx, FullCheckoutParams p, CancellationToken ct)
    {
        using var res = await ctx.Http.GetAsync(p.BaseUrl + "/login", ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Checkout authentication failed with HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Authenticated shopper for checkout");
        return new AddToCart(1);
    }

    private static async Task<CheckoutStep?> DoAddToCart(ScenarioContext ctx, FullCheckoutParams p, AddToCart a, CancellationToken ct)
    {
        using var res = await ctx.Http.PostAsync(p.BaseUrl + $"/cart/items?productId={a.Index}&quantity=1", null, ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Failed adding product {ProductId} to cart: HTTP {StatusCode}", a.Index, (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Added product {ProductId} ({Index}/{Total}) to cart", a.Index, a.Index, p.Items);
        return a.Index < p.Items ? a with { Index = a.Index + 1 } : new ViewCart();
    }

    private static async Task<CheckoutStep?> DoViewCart(ScenarioContext ctx, FullCheckoutParams p, CancellationToken ct)
    {
        using var res = await ctx.Http.GetAsync(p.BaseUrl + "/cart", ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Failed loading cart: HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Loaded cart before applying coupon");
        return new ApplyCoupon();
    }

    private static async Task<CheckoutStep?> DoApplyCoupon(ScenarioContext ctx, FullCheckoutParams p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.PostAsync(p.BaseUrl + $"/cart/coupon?code={Uri.EscapeDataString(p.CouponCode)}", null, ct);
        sw.Stop();
        if ((int)res.StatusCode == 400)
        {
            ctx.Logger.LogWarning("Coupon {CouponCode} rejected (HTTP 400); continuing to stock reservation", p.CouponCode);
            return new ReserveStock();
        }
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Coupon validation failed with HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        if (sw.ElapsedMilliseconds >= 200)
            ctx.Logger.LogWarning("Slow coupon validation for {CouponCode} took {ElapsedMs} ms", p.CouponCode, sw.ElapsedMilliseconds);
        else
            ctx.Logger.LogInformation("Applied coupon {CouponCode} in {ElapsedMs} ms", p.CouponCode, sw.ElapsedMilliseconds);
        return new ReserveStock();
    }

    private static async Task<CheckoutStep?> DoReserveStock(ScenarioContext ctx, FullCheckoutParams p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.PostAsync(p.BaseUrl + "/orders/reserve", null, ct);
        sw.Stop();
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Stock reservation failed with HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        if (sw.ElapsedMilliseconds >= 250)
            ctx.Logger.LogWarning("Slow stock reservation (lock contention) took {ElapsedMs} ms", sw.ElapsedMilliseconds);
        else
            ctx.Logger.LogInformation("Reserved inventory in {ElapsedMs} ms", sw.ElapsedMilliseconds);
        return new CompletePayment();
    }

    private static async Task<CheckoutStep?> DoCompletePayment(ScenarioContext ctx, FullCheckoutParams p, CancellationToken ct)
    {
        using var res = await ctx.Http.PostAsync(p.BaseUrl + "/pay", null, ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Checkout payment failed with HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Checkout payment completed");
        return null;
    }
}
