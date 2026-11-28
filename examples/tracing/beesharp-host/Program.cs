using Beesharp;
using Beesharp.Tracing.Host.Scenarios;

var builder = BeesharpApp.CreateBuilder(args);

var shopApiUrl = builder.Configuration["ShopApiUrl"] ?? "http://localhost:5292";

// Compiled C# 15 union step-machine scenarios (inspectable read-only in /editor via Portable PDBs).
builder.AddScenario<CatalogBrowseScenario>();
builder.AddScenario<FullCheckoutScenario>();
builder.AddScenario<ShopJourneyScenario>();
builder.AddScenario<ReturnsRefundScenario>();

// Strongly typed default preset — pre-loaded in the UI so pressing Play starts immediately.
builder.AddPreset("Shop full mix", plan => plan
    .Duration(TimeSpan.FromMinutes(2))
    .MaxConcurrency(60)
    .Timeout(TimeSpan.FromSeconds(30))
    .Add<CatalogBrowseScenario>(
        new CatalogBrowseParams(BaseUrl: shopApiUrl, Query: "honey", Products: 3),
        ratePerMinute: 90,
        label: "Catalog browse")
    .Add<FullCheckoutScenario>(
        new FullCheckoutParams(BaseUrl: shopApiUrl, Items: 2, CouponCode: "HONEY10"),
        ratePerMinute: 45,
        label: "Full checkout")
    .AddBurst<ShopJourneyScenario>(
        new ShopJourneyParams(BaseUrl: shopApiUrl, Products: 3),
        beesPerBurst: 8,
        burstsPerMinute: 4,
        label: "Shop journey")
    .Add<ReturnsRefundScenario>(
        new ReturnsRefundParams(BaseUrl: shopApiUrl, ProductId: 2),
        ratePerMinute: 20,
        label: "Returns & refund"))
    .AsDefault();

await builder.Build().RunAsync();
