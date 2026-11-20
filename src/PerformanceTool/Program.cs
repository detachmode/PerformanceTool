using PerformanceTool.Components;
using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.BuiltIn;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<ScenarioCatalog>();
builder.Services.AddSingleton<PresetStore>();
builder.Services.AddScoped<LoadRunner>();

// Scenarios: add yours here (see docs/brain/0003-code-defined-scenarios.md).
builder.Services.AddScenario<HttpGetScenario>();
builder.Services.AddScenario<HttpPostJsonScenario>();
builder.Services.AddScenario<CustomRequestScenario>();
builder.Services.AddScenario<UserJourneyScenario>();

var app = builder.Build();

// Fail fast: building the catalog builds every scenario's parameter spec, which throws for unsupported types (brain 0007).
app.Services.GetRequiredService<ScenarioCatalog>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Machine-readable scenario catalog (id, name, description, JSON Schema of the parameters) for the CLI and coding harnesses.
app.MapGet("/api/scenarios", (ScenarioCatalog catalog) => catalog.All.Select(s => new
{
    s.Id,
    s.Name,
    s.Description,
    Parameters = s.Spec.ToJsonSchema(),
}));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
