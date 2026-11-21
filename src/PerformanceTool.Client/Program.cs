using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PerformanceTool.Client;
using PerformanceTool.Client.Services;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.BuiltIn;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<ScenarioCatalog>();
builder.Services.AddSingleton<LogsPopupState>();
builder.Services.AddSingleton<ViewSettings>();
builder.Services.AddSingleton<EnvironmentsPopupState>();

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
    // Debug: "?burst=100" makes the bursts of the seeded run that big (brain 0042). "?simulate=3h" (or 90m, 600s) pre-generates a run that long, to test the UI at scale (brain 0021).
    case "Fake": builder.Services.AddSingleton<IRunSource>(sp => new FakeRunSource(sp.GetRequiredService<ScenarioCatalog>(),
        SimulateSeconds(sp.GetRequiredService<NavigationManager>().Uri), null, BurstActors(sp.GetRequiredService<NavigationManager>().Uri))); break;
    case "Api": builder.Services.AddSingleton<IRunSource, ApiRunSource>(); break;
    default: throw new InvalidOperationException($"Unknown DataSource '{dataSource}'. Use \"Fake\" or \"Api\".");
}

// Environments (brain 0033) follow the same switch.
if (dataSource == "Api") builder.Services.AddSingleton<IEnvironmentSource, ApiEnvironmentSource>();
else builder.Services.AddSingleton<IEnvironmentSource, FakeEnvironmentSource>();

var host = builder.Build();

// Fail fast: building the catalog builds every scenario's parameter spec, which throws for unsupported types (brain 0007).
host.Services.GetRequiredService<ScenarioCatalog>();

await host.RunAsync();

static double SimulateSeconds(string uri)
{
    var m = Regex.Match(uri, @"[?&]simulate=(\d+)([smh])");
    if (!m.Success) return 0;
    return double.Parse(m.Groups[1].Value) * m.Groups[2].Value switch { "h" => 3600, "m" => 60, _ => 1 };
}

static int BurstActors(string uri)
{
    var m = Regex.Match(uri, @"[?&]burst=(\d+)");
    return m.Success ? Math.Clamp(int.Parse(m.Groups[1].Value), 1, 1000) : 0;
}
