// A tiny service under test with ordinary OpenTelemetry tracing, nothing Beesharp-specific.
// It joins Beesharp's trace through the incoming traceparent. OTEL_SERVICE_NAME and OTEL_EXPORTER_OTLP_ENDPOINT come from compose.
using System.Diagnostics;
using OpenTelemetry.Trace;

var db = new ActivitySource("ShopApi.Database");

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("payment", c => c.BaseAddress = new Uri(builder.Configuration["PaymentUrl"] ?? "http://localhost:8080"));
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource(db.Name)
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddOtlpExporter());

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ShopApi");

// A fake database query as its own span; now and then it is slow, so the trace shows why a call was slow.
async Task Query(string sql, int minMs, int maxMs, double slowChance = 0, int slowMinMs = 400, int slowMaxMs = 900)
{
    using var span = db.StartActivity(sql, ActivityKind.Client);
    span?.SetTag("db.system.name", "postgresql");
    span?.SetTag("db.query.text", sql);
    var slow = slowChance > 0 && Random.Shared.NextDouble() < slowChance;
    var delayMs = slow ? Random.Shared.Next(slowMinMs, slowMaxMs + 1) : Random.Shared.Next(minMs, maxMs + 1);
    if (slow)
    {
        if (delayMs >= 400) span?.SetTag("demo.slow_query", true);
        log.LogWarning("Slow DB query {Sql} took {ElapsedMs} ms", sql, delayMs);
    }
    await Task.Delay(delayMs);
}

app.MapGet("/login", async (string? user) =>
{
    var username = string.IsNullOrWhiteSpace(user) ? "shopper" : user;
    await Query("SELECT user", 15, 40);
    log.LogInformation("User {User} logged in", username);
    return "ok";
});

app.MapGet("/categories", async () =>
{
    await Query("SELECT id, name FROM categories", 4, 12);
    var categories = new[] { "honey", "beeswax", "propolis", "pollen", "candles", "gifts" };
    log.LogInformation("Loaded {CategoryCount} catalog categories", categories.Length);
    return categories;
});

app.MapGet("/search", async (string? q) =>
{
    var term = string.IsNullOrWhiteSpace(q) ? "*" : q;
    await Query("SELECT products WHERE name LIKE @q", 30, 70, 0.05);
    var results = new[] { 1, 2, 3, 4, 5 };
    log.LogInformation("Catalog search for {Query} returned {ResultCount} products", term, results.Length);
    return results;
});

app.MapGet("/products/{id:int}", async (int id) =>
{
    await Query("SELECT product", 20, 50, 0.10);
    log.LogInformation("Loaded product {ProductId}", id);
    return new { id, name = $"Product {id}" };
});

app.MapGet("/products/{id:int}/reviews", async (int id) =>
{
    await Query("SELECT rating, comment FROM reviews WHERE product_id = @id", 10, 28);
    log.LogInformation("Loaded reviews for product {ProductId}", id);
    return new[]
    {
        new { author = "Maja", rating = 5, comment = "Pure and floral." },
        new { author = "Bram", rating = 4, comment = "Fast shipping." },
    };
});

app.MapGet("/recommendations/{id:int}", async (int id) =>
{
    await Query("SELECT related_product_id FROM recommendations WHERE product_id = @id", 30, 80, 0.12, 280, 520);
    var related = new[] { id + 1, id + 2, id + 3 };
    log.LogInformation("Generated {RecommendationCount} recommendations for product {ProductId}", related.Length, id);
    return related;
});

app.MapPost("/cart/items", async (int? productId, int? quantity) =>
{
    var id = productId ?? 1;
    var qty = quantity ?? 1;
    await Query("INSERT INTO cart_items (product_id, qty) VALUES (@id, @qty)", 18, 55);
    log.LogInformation("Added {Quantity}x product {ProductId} to cart", qty, id);
    return Results.Ok(new { productId = id, quantity = qty });
});

app.MapGet("/cart", async () =>
{
    await Query("SELECT product_id, qty FROM cart_items", 8, 22);
    log.LogInformation("Loaded shopping cart");
    return new { items = new[] { new { productId = 1, qty = 2 } }, total = 24.50m };
});

app.MapPost("/cart/coupon", async (string? code) =>
{
    var coupon = string.IsNullOrWhiteSpace(code) ? "HONEY10" : code;
    await Query("SELECT discount_pct FROM coupons WHERE code = @code", 35, 95, 0.05, 220, 380);
    if (Random.Shared.NextDouble() < 0.04)
    {
        log.LogWarning("Rejected invalid coupon {CouponCode}", coupon);
        return Results.BadRequest(new { error = "Invalid or expired coupon", code = coupon });
    }
    log.LogInformation("Applied coupon {CouponCode} ({DiscountPct}% off)", coupon, 10);
    return Results.Ok(new { code = coupon, discountPct = 10 });
});

app.MapPost("/orders/reserve", async () =>
{
    await Query("UPDATE inventory SET reserved = reserved + 1 WHERE stock > reserved", 45, 120, 0.10, 300, 550);
    if (Random.Shared.NextDouble() < 0.03)
    {
        log.LogError("Stock reservation conflict (409): item out of stock");
        return Results.Conflict(new { error = "Out of stock" });
    }
    var reservationId = Guid.NewGuid().ToString("N")[..8];
    log.LogInformation("Reserved inventory for reservation {ReservationId}", reservationId);
    return Results.Ok(new { reservationId });
});

app.MapPost("/pay", async (IHttpClientFactory http) =>
{
    await Query("INSERT order", 10, 30);
    using var res = await http.CreateClient("payment").PostAsync("/charge", null);
    if (!res.IsSuccessStatusCode)
    {
        log.LogError("Order payment failed: payment-api returned {StatusCode}", (int)res.StatusCode);
        return Results.Problem("payment failed", statusCode: 502);
    }
    log.LogInformation("Order payment completed");
    return Results.Ok("paid");
});

app.MapPost("/returns", async (IHttpClientFactory http) =>
{
    await Query("INSERT INTO returns (order_id, reason) VALUES (@orderId, @reason)", 90, 200);
    using var res = await http.CreateClient("payment").PostAsync("/refund", null);
    if (!res.IsSuccessStatusCode)
    {
        log.LogError("Return refund failed: payment-api returned {StatusCode}", (int)res.StatusCode);
        return Results.Problem("refund failed", statusCode: 502);
    }
    log.LogInformation("Processed return and issued refund");
    return Results.Ok(new { status = "refunded" });
});

// Only used as payment-api: a downstream service that sometimes fails.
app.MapPost("/charge", async () =>
{
    var delayMs = Random.Shared.Next(50, 121);
    await Task.Delay(delayMs);
    if (Random.Shared.NextDouble() < 0.03)
    {
        log.LogError("Payment charge rejected by gateway (503) after {ElapsedMs} ms", delayMs);
        return Results.StatusCode(503);
    }
    log.LogInformation("Payment charge authorized in {ElapsedMs} ms", delayMs);
    return Results.Ok("charged");
});

app.MapPost("/refund", async () =>
{
    var delayMs = Random.Shared.Next(70, 181);
    await Task.Delay(delayMs);
    if (Random.Shared.NextDouble() < 0.04)
    {
        log.LogError("Refund rejected by payment gateway (503) after {ElapsedMs} ms", delayMs);
        return Results.StatusCode(503);
    }
    log.LogInformation("Refund settled in {ElapsedMs} ms", delayMs);
    return Results.Ok("refunded");
});

app.Run();
