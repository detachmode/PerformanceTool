// Scenario for a full checkout flow: login, add items to cart, view cart, apply coupon, reserve stock, pay.
using System.Diagnostics;

public record FullCheckoutParams(
    [property: Description("Base URL of shop-api")]
    string BaseUrl = "http://shop-api:8080",
    [property: Description("How many items to add to the cart")]
    [property: Range(1, 10)]
    int Items = 2,
    [property: Description("Promo coupon code to apply")]
    string CouponCode = "HONEY10");

public record Authenticate;
public record AddToCart(int Index);
public record ViewCart;
public record ApplyCoupon;
public record ReserveStock;
public record CompletePayment;
public union CheckoutStep(Authenticate, AddToCart, ViewCart, ApplyCoupon, ReserveStock, CompletePayment);

Scenario.DefineSteps<FullCheckoutParams, CheckoutStep>(
    "Full checkout",
    start: _ => new Authenticate(),
    run: async (step, p, ctx) => step switch
    {
        Authenticate => await DoAuthenticate(ctx, p),
        AddToCart a => await DoAddToCart(ctx, p, a),
        ViewCart => await DoViewCart(ctx, p),
        ApplyCoupon => await DoApplyCoupon(ctx, p),
        ReserveStock => await DoReserveStock(ctx, p),
        CompletePayment => await DoCompletePayment(ctx, p),
    });

async Task<CheckoutStep?> DoAuthenticate(ScenarioContext ctx, FullCheckoutParams p)
{
    using var res = await ctx.Http.GetAsync(p.BaseUrl + "/login", ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Checkout authentication failed with HTTP {StatusCode}", (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Authenticated shopper for checkout");
    return new AddToCart(1);
}

async Task<CheckoutStep?> DoAddToCart(ScenarioContext ctx, FullCheckoutParams p, AddToCart a)
{
    using var res = await ctx.Http.PostAsync(p.BaseUrl + $"/cart/items?productId={a.Index}&quantity=1", null, ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Failed adding product {ProductId} to cart: HTTP {StatusCode}", a.Index, (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Added product {ProductId} ({Index}/{Total}) to cart", a.Index, a.Index, p.Items);
    return a.Index < p.Items ? a with { Index = a.Index + 1 } : new ViewCart();
}

async Task<CheckoutStep?> DoViewCart(ScenarioContext ctx, FullCheckoutParams p)
{
    using var res = await ctx.Http.GetAsync(p.BaseUrl + "/cart", ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Failed loading cart: HTTP {StatusCode}", (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Loaded cart before applying coupon");
    return new ApplyCoupon();
}

async Task<CheckoutStep?> DoApplyCoupon(ScenarioContext ctx, FullCheckoutParams p)
{
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.PostAsync(p.BaseUrl + $"/cart/coupon?code={Uri.EscapeDataString(p.CouponCode)}", null, ctx.CancellationToken);
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

async Task<CheckoutStep?> DoReserveStock(ScenarioContext ctx, FullCheckoutParams p)
{
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.PostAsync(p.BaseUrl + "/orders/reserve", null, ctx.CancellationToken);
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

async Task<CheckoutStep?> DoCompletePayment(ScenarioContext ctx, FullCheckoutParams p)
{
    using var res = await ctx.Http.PostAsync(p.BaseUrl + "/pay", null, ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Checkout payment failed with HTTP {StatusCode}", (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Checkout payment completed");
    return null;
}
