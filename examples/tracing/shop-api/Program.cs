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

// A fake database query as its own span; now and then it is slow, so the trace shows why a call was slow.
async Task Query(string sql, int minMs, int maxMs, double slowChance = 0)
{
    using var span = db.StartActivity(sql, ActivityKind.Client);
    span?.SetTag("db.system.name", "postgresql");
    span?.SetTag("db.query.text", sql);
    var slow = Random.Shared.NextDouble() < slowChance;
    if (slow) span?.SetTag("demo.slow_query", true);
    await Task.Delay(slow ? Random.Shared.Next(400, 900) : Random.Shared.Next(minMs, maxMs));
}

app.MapGet("/login", async () => { await Query("SELECT user", 15, 40); return "ok"; });

app.MapGet("/search", async (string? q) => { await Query("SELECT products WHERE name LIKE @q", 30, 70, 0.05); return new[] { 1, 2, 3, 4, 5 }; });

app.MapGet("/products/{id:int}", async (int id) => { await Query("SELECT product", 20, 50, 0.10); return new { id, name = $"Product {id}" }; });

app.MapPost("/pay", async (IHttpClientFactory http) =>
{
    await Query("INSERT order", 10, 30);
    using var res = await http.CreateClient("payment").PostAsync("/charge", null);
    return res.IsSuccessStatusCode ? Results.Ok("paid") : Results.Problem("payment failed", statusCode: 502);
});

// Only used as payment-api: a downstream service that sometimes fails.
app.MapPost("/charge", async () =>
{
    await Task.Delay(Random.Shared.Next(50, 120));
    return Random.Shared.NextDouble() < 0.03 ? Results.StatusCode(503) : Results.Ok("charged");
});

app.Run();
