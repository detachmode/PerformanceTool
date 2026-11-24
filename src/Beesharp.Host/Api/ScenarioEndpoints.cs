using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Api;

/// <summary>REST for the scenario catalog.</summary>
public static class ScenarioEndpoints
{
    public static IEndpointRouteBuilder MapScenarioEndpoints(this IEndpointRouteBuilder app)
    {
        // Machine-readable scenario catalog (id, name, description, JSON Schema of the parameters) for the CLI, UI and coding harnesses.
        app.MapGet("/api/scenarios", (ScenarioCatalog catalog, ScriptScenarioStore scripts) => catalog.All.Select(s => new ScenarioInfoDto(
            s.Id,
            s.Name,
            s.Description,
            catalog.IsCustom(s.Id),
            scripts.FindScriptIdForScenario(s.Id),
            s.Spec.ToJsonSchema(),
            s.Steps.Count > 0 ? s.Steps : null)));
        return app;
    }
}
