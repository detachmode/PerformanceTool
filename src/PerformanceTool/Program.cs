using System.Text.Json.Nodes;
using PerformanceTool.Services;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.BuiltIn;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ScenarioCatalog>();
builder.Services.AddSingleton<PresetStore>();
builder.Services.AddSingleton<EnvironmentStore>();
builder.Services.AddSingleton<LoadRunner>();
builder.Services.AddSingleton<ScriptScenarioStore>();
builder.Services.AddSingleton<RunManager>();

// Scenarios: add yours here (see docs/brain/0003-code-defined-scenarios.md).
builder.Services.AddScenario<HttpGetScenario>();
builder.Services.AddScenario<HttpPostJsonScenario>();
builder.Services.AddScenario<CustomRequestScenario>();
builder.Services.AddScenario<UserJourneyScenario>();

var app = builder.Build();

// Fail fast: building the catalog builds every scenario's parameter spec, which throws for unsupported types (brain 0007).
app.Services.GetRequiredService<ScenarioCatalog>();
await app.Services.GetRequiredService<ScriptScenarioStore>().LoadAllFromDiskAsync();

if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseHttpsRedirection();

// The Blazor WebAssembly client (src/PerformanceTool.Client): its _framework files and wwwroot come from the project reference
// as static web assets, which MapStaticAssets serves (fingerprinted, pre-compressed). Brain 0015.
app.MapStaticAssets();

// Machine-readable scenario catalog (id, name, description, JSON Schema of the parameters) for the CLI, UI and coding harnesses.
app.MapGet("/api/scenarios", (ScenarioCatalog catalog, ScriptScenarioStore scripts) => catalog.All.Select(s => new
{
    s.Id,
    s.Name,
    s.Description,
    IsCustom = catalog.IsCustom(s.Id),
    ScriptId = scripts.FindScriptIdForScenario(s.Id),
    Parameters = s.Spec.ToJsonSchema(),
}));

// Custom C# script scenarios (brain 0040): upload/compile .csx/.cs scripts and test-run a single execution.
app.MapGet("/api/scripts", (ScriptScenarioStore store) => store.List());
app.MapGet("/api/scripts/{id}", (string id, ScriptScenarioStore store) =>
    store.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());
app.MapPost("/api/scripts", async (HttpRequest request, ScriptScenarioStore store, CancellationToken ct) =>
{
    string? id = null;
    string? fileName = null;
    string code;

    if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync(ct);
        id = form["id"].FirstOrDefault();
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "Upload a non-empty .csx or .cs file." });
        fileName = file.FileName;
        using var reader = new StreamReader(file.OpenReadStream());
        code = await reader.ReadToEndAsync(ct);
    }
    else
    {
        var body = await request.ReadFromJsonAsync<UploadScriptRequest>(ct);
        if (body is null)
            return Results.BadRequest(new { error = "Provide a JSON body with { fileName, code } or multipart/form-data file." });
        id = body.Id;
        fileName = body.FileName;
        code = body.Code ?? "";
    }

    var result = await store.CompileAndRegisterAsync(id, fileName, code, persist: true, ct);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapDelete("/api/scripts/{id}", (string id, ScriptScenarioStore store) =>
    store.Delete(id) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/scripts/{id}/test-run", async (string id, TestRunScriptRequest? body, ScriptScenarioStore store, CancellationToken ct) =>
{
    try
    {
        var result = await store.TestRunAsync(id, body?.Parameters, body?.EnvironmentName, body?.TimeoutSeconds ?? 30, ct);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Live runs (for Live mode in the UI and REST clients).
app.MapGet("/api/runs/current", (RunManager runs) => runs.GetCurrent());
app.MapPost("/api/runs/start", async (StartRunRequest body, RunManager runs, CancellationToken ct) =>
{
    try { return Results.Ok(await runs.StartAsync(body.Plan, body.EnvironmentName, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapPost("/api/runs/stop", (RunManager runs) => runs.StopAsync());

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

public sealed record UploadScriptRequest(string? Id, string? FileName, string? Code);
public sealed record TestRunScriptRequest(JsonObject? Parameters = null, string? EnvironmentName = null, int TimeoutSeconds = 30);
public sealed record StartRunRequest(LoadPlan Plan, string? EnvironmentName = null);
