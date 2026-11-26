# Beesharp API & Scripting Guide

This guide covers how to define scenarios in C#, track and name/tag HTTP calls, work with environments and runtime data, configure load plans & presets, embed Beesharp in your own .NET app, and drive it via REST or CLI.

---

## 1. Defining Scenarios

A **scenario** is a complete user story executed by each **bee** (actor). Scenarios can be uploaded as `.cs` scripts (`scripts/` folder or the web Editor) or compiled into a custom host using `Beesharp.Host`.

### 1.1 Simple scenario (`Scenario.Define`)

The UI editor is generated automatically from your parameter record (`[Description]`, `[Range]`, `[Secret]`, enums, booleans, defaults).

```csharp
public record SearchParams(
    [property: Description("Base URL of the target service (supports {{Placeholders}})")]
    string BaseUrl = "{{ApiUrl}}",

    [property: Description("Search term")]
    string Query = "honey",

    [property: Range(1, 10)]
    int PageSize = 5
);

Scenario.Define<SearchParams>("Catalog Search", async (p, ctx) =>
{
    var url = $"{p.BaseUrl.TrimEnd('/')}/api/products?q={Uri.EscapeDataString(p.Query)}&limit={p.PageSize}";
    using var response = await ctx.Http.GetAsync(url, ctx.CancellationToken);
    response.EnsureSuccessStatusCode();

    ctx.Logger.LogInformation("Search for {Query} returned {Status}", p.Query, (int)response.StatusCode);
})
.WithDescription("Searches the product catalog and verifies HTTP 200.")
.SlowAbove(TimeSpan.FromMilliseconds(350))
.OnStartup(async (p, ctx) =>
{
    // Runs once per scenario entry before bees start
    ctx.State["startedAt"] = DateTimeOffset.UtcNow.ToString("O");
    await Task.CompletedTask;
})
.OnTeardown(async (p, ctx) =>
{
    // Runs once per scenario entry when the run finishes
    ctx.Logger.LogInformation("Catalog Search teardown complete");
    await Task.CompletedTask;
});
```

### 1.2 Multi-step scenarios (`Scenario.DefineSteps` with C# 15 `union`)

Declare your steps up front as a C# 15 `union` of step records. Each step returns an `IAsyncEnumerable<TStep>` and dynamically yields the next step—supporting **self-repeats** (retries, browsing multiple items), **backward loops**, and **forward skips**.

Declared steps unlock per-step failure counts, the chronological stepper in bee details, step filtering in `/logs` and `/calls`, and the **Swarm: Dead or Alive** 3D matrix on `/3d`.

```csharp
record AuthStep;
record CatalogStep(string Query, int Visit = 1);
record ReserveStep(string Sku, int Attempt = 1);
record OrderStep(string Sku);

union ShopStep(AuthStep, CatalogStep, ReserveStep, OrderStep);

Scenario.DefineSteps<ShopParams, ShopStep>(
    "Shop Checkout",
    start: _ => new AuthStep(),
    run: (step, p, ctx) => step switch
    {
        AuthStep => AuthAsync(p, ctx),
        CatalogStep c => CatalogAsync(c, p, ctx),
        ReserveStep r => ReserveAsync(r, p, ctx),
        OrderStep o => OrderAsync(o, p, ctx),
    })
    .WithDescription("Auth -> Catalog (repeats) -> Reserve (retries) -> Order.");

static async IAsyncEnumerable<ShopStep> CatalogAsync(CatalogStep c, ShopParams p, ScenarioContext ctx)
{
    using var resp = await ctx.Http.GetAsync($"{p.BaseUrl}/api/products?q={c.Query}", ctx.CancellationToken);
    resp.EnsureSuccessStatusCode();

    // Self-repeat: browse up to 3 items (Step 2 -> Step 2)
    if (c.Visit < 3 && Random.Shared.Next(100) < 40)
    {
        yield return new CatalogStep(c.Query, c.Visit + 1);
        yield break;
    }

    yield return new ReserveStep($"SKU-{Random.Shared.Next(1, 20):000}");
}
```

### 1.3 Local IDE debugging (`.cs` file-based apps)

When using the `.beesharp/` SDK drop, you can debug any `.cs` script directly in your IDE using `DebugRunAsync` (brain 0057).

---

## 2. HTTP Calls: Automatic Tracking, Naming, Tagging & Thresholds

Every HTTP request made through `ctx.Http`—or through **any** `HttpClient` created inside a bee (including typed clients, Refit, and SDKs)—is automatically recorded, normalized (IDs/UUIDs/slugs collapsed into `{id}`), and attributed to the active bee and step.

### 2.1 Naming & tagging calls (same endpoint, different payloads)

When one endpoint (e.g., `POST /graphql`, `POST /api/rpc`, or `POST /api/inventory/reserve`) performs different actions depending on the request payload, you can control how calls are named and grouped in `/calls`, CLI, and `/3d` (`6. Calls Matrix`):

#### Option A: Tag the call (keeps the normalized endpoint + appends `[Tag]`)

```csharp
// Ambient scope — works with PostAsync/GetAsync, typed clients, Refit, SDKs
using (ctx.CallScope(tag: "CreateOrder"))
{
    await ctx.Http.PostAsJsonAsync($"{p.BaseUrl}/api/rpc", createPayload, ctx.CancellationToken);
}
// Recorded endpoint: "POST /api/rpc [CreateOrder]"

// Or per HttpRequestMessage:
using var req = new HttpRequestMessage(HttpMethod.Post, $"{p.BaseUrl}/api/rpc")
    .WithCallTag("CancelOrder");
req.Content = JsonContent.Create(cancelPayload);
await ctx.Http.SendAsync(req, ctx.CancellationToken);
// Recorded endpoint: "POST /api/rpc [CancelOrder]"
```

#### Option B: Give the call an explicit custom name (replaces the endpoint label)

```csharp
// Ambient scope:
using (ctx.CallScope("Order · VIP Express"))
{
    await ctx.Http.PostAsync($"{p.BaseUrl}/api/orders", content: null, ctx.CancellationToken);
}

// Or per HttpRequestMessage:
using var req = new HttpRequestMessage(HttpMethod.Post, $"{p.BaseUrl}/api/orders")
    .AsCall("Order · Standard");
await ctx.Http.SendAsync(req, ctx.CancellationToken);
```

You can also combine both (`AsCall("GraphQL").WithCallTag("GetCart")` or `ctx.CallScope("GraphQL", tag: "GetCart")` → `"GraphQL [GetCart]"`). The original HTTP method and full raw URL are always preserved in the call inspector.

### 2.2 Slow thresholds & timing splits

By default, a call is classified as **slow** above `2×` the typical duration for its endpoint. You can override this at three levels (most specific wins):

```csharp
// 1. Scenario-wide or per-endpoint pattern on the builder:
Scenario.Define<MyParams>("Checkout", ...)
    .SlowAbove(TimeSpan.FromMilliseconds(400))
    .SlowAbove("POST /api/orders*", TimeSpan.FromMilliseconds(800));

// 2. Ambient scope:
using (ctx.CallScope(name: "Reserve Stock", slowAbove: TimeSpan.FromMilliseconds(250)))
{
    await ctx.Http.PostAsync($"{p.BaseUrl}/api/inventory/reserve", null, ctx.CancellationToken);
}

// 3. Per request (+ optional TTFB vs body-read timing split):
using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.BaseUrl}/api/export")
    .SlowAbove(TimeSpan.FromSeconds(2))
    .MeasureTimingSplit();
await ctx.Http.SendAsync(req, ctx.CancellationToken);
```

### 2.3 Measuring non-HTTP work

For database queries, gRPC, or local computation, use `ctx.MeasureCallAsync`:

```csharp
await ctx.MeasureCallAsync("Redis · WarmCache", async () =>
{
    await Task.Delay(25, ctx.CancellationToken);
});
```

---

## 3. Logging, Environments & Runtime Data

### 3.1 Per-bee logging (`ctx.Logger`)

Every bee has its own scoped `ILogger`. Logs are persisted with the run and filterable by scenario, step, bee, level, and search text on `/logs` and in `beesharp runs logs`:

```csharp
ctx.Logger.LogInformation("Reserved {Sku} in {Ms} ms", sku, elapsedMs);
ctx.Logger.LogWarning("Retrying payment attempt {Attempt}", attempt);
ctx.Logger.LogError("Order rejected: {Reason}", reason);
```

### 3.2 Environments & `{{Placeholders}}`

Environments are JSON files in `environments/` holding key-value pairs (such as `ApiUrl`, `TenantId`, `ApiKey`).
- Any `{{Key}}` in scenario parameter values is resolved when the run starts.
- Read values directly in code via `ctx.Environment["Key"]` (or `ctx.Environment.TryGetValue("Key", out var val)`).
- Secret parameter fields also support `${env:OS_VAR_NAME}` resolved from operating-system environment variables.

### 3.3 Runtime Data (`RunBag` & `State`) + Preflight / Shutdown

- **`ctx.RunBag`**: Shared across the whole run (written by preflight, scenarios, and read by shutdown).
- **`ctx.State`**: Scoped to a single scenario entry (useful for tokens or setup data created in `OnStartup`).
- **Preflight** (`scripts/preflight/`): Runs before scenarios begin; if it fails, scenarios do not start.
- **Shutdown** (`scripts/shutdown/`): Always runs when a run ends—even after manual Stop or a failed preflight—for reliable cleanup.

```csharp
// Inside a scenario:
ctx.State["token"] = token;
ctx.RunBag["lastOrderId"] = orderId;
var sharedSeed = ctx.RunBag.Get<string>("seedBatchId");
```

---

## 4. Load Plans & Presets

A **load plan** (`LoadPlan`) defines which scenario entries run, their spawning pattern (`Linear` or `Bursts`), duration, concurrency cap, timeout, and optional preflight/shutdown scripts. Saved plans live as JSON files in `presets/`.

```json
{
  "name": "Demo Shop (3D)",
  "plan": {
    "scenarios": [
      {
        "scenarioId": "script-demo-shop",
        "label": "Browse Catalog",
        "ratePerMinute": 240,
        "pattern": "Linear",
        "maxInFlight": 80,
        "parameters": {
          "baseUrl": "{{ShopUrl}}",
          "mode": "Browse",
          "query": "honey"
        }
      },
      {
        "scenarioId": "script-demo-shop",
        "label": "Flash Search Burst",
        "ratePerMinute": 180,
        "pattern": "Bursts",
        "beesPerBurst": 45,
        "burstsPerMinute": 4,
        "maxInFlight": 100,
        "parameters": {
          "baseUrl": "{{ShopUrl}}",
          "mode": "SearchBurst",
          "query": "flash-sale"
        }
      }
    ],
    "durationSeconds": 300,
    "maxConcurrency": 350,
    "timeoutSeconds": 30,
    "preflightScriptId": null,
    "shutdownScriptId": null
  }
}
```

- **`Linear`:** Starts `ratePerMinute` new bees evenly spread across each minute.
- **`Bursts`:** Starts `beesPerBurst` bees simultaneously, `burstsPerMinute` times per minute (effective rate = `beesPerBurst × burstsPerMinute`).
- **Open-model scheduling:** Bees start on schedule regardless of response time; if `maxConcurrency` or `maxInFlight` is reached, due bees are counted as **dropped**.

---

## 5. Embedding Beesharp (`Beesharp.Host`)

You can embed the full Beesharp engine, storage, and Blazor UI inside a plain .NET console app (`Beesharp.Host`):

```csharp
using Beesharp;

var builder = BeesharpApp.CreateBuilder(args);

builder.AddScenario<MyCheckoutScenario>();

var app = builder.Build();
await app.RunAsync();
```

---

## 6. REST API Quick Reference

All entry, step, and bee sequence indexes in the REST API are **0-based**.

| Area | Endpoint | Purpose |
|---|---|---|
| Scenarios | `GET /api/scenarios` | List built-in and compiled script scenarios + parameter schemas |
| Scripts | `GET /api/scripts`, `POST /api/scripts`, `POST /api/scripts/test` | Manage and test-run C# scenario scripts |
| Plan | `GET /api/plan`, `PUT /api/plan` | Read or replace the active Live load plan |
| Presets | `GET /api/presets`, `PUT /api/presets/{name}`, `POST /api/presets/{name}/load` | List, save, or load a preset into the Live plan |
| Environments | `GET /api/environments`, `PUT /api/environments/{name}` | Manage environment key-value definitions |
| Run Control | `POST /api/runs/start`, `POST /api/runs/stop`, `POST /api/runs/pause`, `POST /api/runs/resume` | Start, stop (marks in-flight bees as canceled), pause, or resume a run |
| Run History | `GET /api/runs?limit=30`, `POST /api/runs/{runId}/load` | List stored runs or load a past run into the UI |
| Run Queries | `GET /api/runs/{runId}/summary` | Overall and per-scenario summary metrics |
| | `GET /api/runs/{runId}/bees?entry=0&limit=5000` | Compact bee records for a run |
| | `GET /api/runs/{runId}/bees/{seq}` | Full bee detail (steps, HTTP calls, headers, logs) |
| | `GET /api/runs/{runId}/calls/endpoints` | Aggregated endpoint/tagged-call latencies (`p50`, `p95`, `max`, slow, failed) |
| | `GET /api/runs/{runId}/calls/detail?endpoint=...` | Individual calls and histogram for one endpoint/tag |
| | `GET /api/runs/{runId}/logs?level=error&q=...` | Filtered, paged logs (`GET .../logs/facets` for counts) |
| | `GET /api/runs/{runId}/runtime` | Run bag and entry bag runtime data |

---

## 7. CLI Reference

For headless CI runs, Parquet `.zip` run bundles, offline inspection, and regression comparisons (`beesharp run`, `beesharp runs summary|calls|errors|logs|bee|compare|timeline`), see [docs/cli.md](cli.md).
