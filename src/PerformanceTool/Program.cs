using PerformanceTool.Services;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.BuiltIn;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ScenarioCatalog>();
builder.Services.AddSingleton<PresetStore>();
builder.Services.AddSingleton<EnvironmentStore>();
builder.Services.AddScoped<LoadRunner>();

// Scenarios: add yours here (see docs/brain/0003-code-defined-scenarios.md).
builder.Services.AddScenario<HttpGetScenario>();
builder.Services.AddScenario<HttpPostJsonScenario>();
builder.Services.AddScenario<CustomRequestScenario>();
builder.Services.AddScenario<UserJourneyScenario>();

var app = builder.Build();

// Fail fast: building the catalog builds every scenario's parameter spec, which throws for unsupported types (brain 0007).
app.Services.GetRequiredService<ScenarioCatalog>();

if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseHttpsRedirection();

// The Blazor WebAssembly client (src/PerformanceTool.Client): its _framework files and wwwroot come from the project reference
// as static web assets, which MapStaticAssets serves (fingerprinted, pre-compressed). Brain 0015.
app.MapStaticAssets();

// Machine-readable scenario catalog (id, name, description, JSON Schema of the parameters) for the CLI and coding harnesses.
app.MapGet("/api/scenarios", (ScenarioCatalog catalog) => catalog.All.Select(s => new
{
    s.Id,
    s.Name,
    s.Description,
    Parameters = s.Spec.ToJsonSchema(),
}));

// Environments (brain 0033): one JSON file each. Minimal REST: list, get, save (create or replace), delete.
app.MapGet("/api/environments", (EnvironmentStore store, CancellationToken ct) => store.ListAsync(ct));
app.MapGet("/api/environments/{name}", async (string name, EnvironmentStore store, CancellationToken ct) =>
    await store.GetAsync(name, ct) is { } e ? Results.Ok(e) : Results.NotFound());
app.MapPut("/api/environments/{name}", async (string name, EnvironmentDefinition environment, EnvironmentStore store, CancellationToken ct) =>
{
    environment.Name = name;
    try { await store.SaveAsync(environment, ct); return Results.NoContent(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/environments/{name}", async (string name, EnvironmentStore store, CancellationToken ct) =>
{
    await store.DeleteAsync(name, ct);
    return Results.NoContent();
});

// Unknown API routes are a real 404, not the client app.
app.Map("/api/{**rest}", () => Results.NotFound());

// Everything else is a client-side route of the WebAssembly app (/editor, ...).
app.MapFallbackToFile("index.html");

app.Run();
