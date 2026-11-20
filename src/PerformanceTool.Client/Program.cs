using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PerformanceTool.Client;
using PerformanceTool.Client.Services;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.BuiltIn;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<ScenarioCatalog>();

// Scenarios: add yours here (see docs/brain/0003-code-defined-scenarios.md).
builder.Services.AddScenario<HttpGetScenario>();
builder.Services.AddScenario<HttpPostJsonScenario>();
builder.Services.AddScenario<CustomRequestScenario>();
builder.Services.AddScenario<UserJourneyScenario>();

// Data source (brain 0015): wwwroot/appsettings.json -> "DataSource": "Fake" (in-browser simulation) or "Api" (not implemented yet).
// One instance per browser tab, so the run survives navigation between pages.
var dataSource = builder.Configuration["DataSource"] ?? "Fake";
switch (dataSource)
{
    case "Fake": builder.Services.AddSingleton<IRunSource, FakeRunSource>(); break;
    case "Api": builder.Services.AddSingleton<IRunSource, ApiRunSource>(); break;
    default: throw new InvalidOperationException($"Unknown DataSource '{dataSource}'. Use \"Fake\" or \"Api\".");
}

var host = builder.Build();

// Fail fast: building the catalog builds every scenario's parameter spec, which throws for unsupported types (brain 0007).
host.Services.GetRequiredService<ScenarioCatalog>();

await host.RunAsync();
