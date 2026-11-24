using Beesharp;
using HostExample;

var builder = BeesharpApp.CreateBuilder(args, new BeesharpAppOptions
{
    IncludeBuiltInScenarios = false,
});

// Plain .NET DI for shared cross-cutting concerns (no special Beesharp interface needed).
builder.Services.AddSingleton<DemoTokenCache>();

// Compiled scenarios with constructor injection.
builder.AddScenario<ShopCheckoutScenario>();

// Strongly typed preset — pre-loaded in the UI so clicking Play runs immediately.
builder.AddPreset("Smoke", plan => plan
    .Duration(TimeSpan.FromMinutes(1))
    .MaxConcurrency(20)
    .Timeout(TimeSpan.FromSeconds(15))
    .Add<ShopCheckoutScenario>(new ShopCheckoutParams(Query: "honey", DelayMs: 40), ratePerMinute: 60))
    .AsDefault();

await builder.Build().RunAsync();
