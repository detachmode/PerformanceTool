using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using PerformanceTool.Client;
using PerformanceTool.Client.Services;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.BuiltIn;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<ScenarioCatalog>();
builder.Services.AddSingleton<LogsPopupState>();
builder.Services.AddSingleton<ViewSettings>();
builder.Services.AddSingleton<EnvironmentsPopupState>();
builder.Services.AddSingleton<CustomScriptsPopupState>();
builder.Services.AddSingleton<ScriptApiClient>();
builder.Services.AddSingleton<PerformanceTool.Client.Components.Assistant.AssistantClient>();
builder.Services.AddSingleton<PerformanceTool.Client.Components.Assistant.AssistantState>();

// Scenarios: add yours here (see docs/brain/0003-code-defined-scenarios.md).
builder.Services.AddScenario<HttpGetScenario>();
builder.Services.AddScenario<HttpPostJsonScenario>();
builder.Services.AddScenario<CustomRequestScenario>();
builder.Services.AddScenario<UserJourneyScenario>();

// Data source (brain 0015, 0040): default comes from wwwroot/appsettings.json ("Fake" or "Api"),
// and the user can toggle Fake <-> Live mode at runtime from the top bar.
var initialFake = !string.Equals(builder.Configuration["DataSource"], "Api", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton(sp => new DataModeState(sp.GetRequiredService<IJSRuntime>(), initialFake));

// Debug: "?burst=100" makes the bursts of the seeded run that big (brain 0044). "?simulate=3h" (or 90m, 600s) pre-generates a run that long, to test the UI at scale (brain 0021).
builder.Services.AddSingleton(sp => new FakeRunSource(
    sp.GetRequiredService<ScenarioCatalog>(),
    SimulateSeconds(sp.GetRequiredService<NavigationManager>().Uri), null,
    BurstBees(sp.GetRequiredService<NavigationManager>().Uri)));
builder.Services.AddSingleton<FakeEnvironmentSource>();
builder.Services.AddSingleton<ApiEnvironmentSource>();
builder.Services.AddSingleton<ApiRunSource>();

builder.Services.AddSingleton<IRunSource, SwitchableRunSource>();
builder.Services.AddSingleton<IEnvironmentSource, SwitchableEnvironmentSource>();

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

static int BurstBees(string uri)
{
    var m = Regex.Match(uri, @"[?&]burst=(\d+)");
    return m.Success ? Math.Clamp(int.Parse(m.Groups[1].Value), 1, 1000) : 0;
}
