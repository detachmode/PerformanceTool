using Beesharp.Ai;
using Beesharp.AiAdapters;
using Beesharp.Api;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Storage;

// The host only wires (brain 0054): the modules are Beesharp.Engine (runner, Live plan), Beesharp.Scripting (script compiler and stores),
// Beesharp.Storage(.DuckDb) (run storage, preset and environment files) and Beesharp.Ai (assistant). REST endpoints live in Api/.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var contentRoot = builder.Environment.ContentRootPath;

builder.Services.AddBeesharpEngine(new BeesharpDirectories
{
    Root = contentRoot,
    Presets = config["Presets:Directory"] ?? "presets",
    Environments = config["Environments:Directory"] ?? "environments",
    Scripts = config["Scripts:Directory"] ?? "scripts",
});
// "Storage:Provider" is DuckDb (default) or Memory; anything else (SQLite was dropped) fails here with a message naming the supported ones.
builder.Services.AddBeesharpStorage(new StorageOptions
{
    Provider = config["Storage:Provider"] ?? StorageOptions.DuckDb,
    DuckDbPath = config["Storage:DuckDbPath"],
    Root = contentRoot,
});

// AI assistant (brain 0047): the module in src/Beesharp.Ai only knows its ports; these adapters connect them to the engine's services.
// Configure with AI__Model, AI__ApiKey, AI__Endpoint (see AGENTS.md). Without a model the assistant reports "not configured".
builder.Services.AddSingleton<IPlanWorkspacePort, PlanWorkspaceAdapter>();
builder.Services.AddSingleton<IRunPort, RunAdapter>();
builder.Services.AddSingleton<IRunHistoryPort, RunHistoryAdapter>();
builder.Services.AddSingleton<IEnvironmentPort, EnvironmentAdapter>();
builder.Services.AddSingleton<IScriptPort, ScriptAdapter>();
builder.Services.AddBeesharpAi(config);

// Scenarios: add yours here (see docs/brain/0003-code-defined-scenarios.md).
builder.Services.AddScenario<HttpGetScenario>();
builder.Services.AddScenario<HttpPostJsonScenario>();
builder.Services.AddScenario<CustomRequestScenario>();
builder.Services.AddScenario<UserJourneyScenario>();

var app = builder.Build();

// Fail fast: building the catalog builds every scenario's parameter spec, which throws for unsupported types (brain 0007).
app.Services.GetRequiredService<ScenarioCatalog>();
await app.Services.LoadBeesharpScriptsAsync();
await app.Services.GetRequiredService<RunManager>().RestoreLatestAsync();
// The restored run's plan is where the Live plan starts (the editor used to adopt it in the browser).
app.Services.GetRequiredService<PlanWorkspace>().SeedIfEmpty(app.Services.GetRequiredService<RunManager>().GetCurrent().Plan);

if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseHttpsRedirection();

// The Blazor WebAssembly client (src/Beesharp.Client): its _framework files and wwwroot come from the project reference
// as static web assets, which MapStaticAssets serves (fingerprinted, pre-compressed). Brain 0015.
app.MapStaticAssets();

app.MapScenarioEndpoints();
app.MapScriptEndpoints();
app.MapPreflightEndpoints();
app.MapShutdownEndpoints();
app.MapRunEndpoints();
app.MapPlanEndpoints();
app.MapEnvironmentEndpoints();
app.MapRunQueryEndpoints();
app.MapBeesharpAi();

// Unknown API routes are a real 404, not the client app.
app.Map("/api/{**rest}", () => Results.NotFound());

// Everything else is a client-side route of the WebAssembly app (/editor, ...): index.html with its asset placeholders filled.
app.MapClientIndex();

app.Run();
