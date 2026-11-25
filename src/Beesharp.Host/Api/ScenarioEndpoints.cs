using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Api;

/// <summary>
/// Endpoint mappings for the machine-readable scenario catalog REST API.
/// </summary>
public static class ScenarioEndpoints
{
    /// <summary>
    /// Maps REST endpoints for querying the registered built-in and custom script scenarios.
    /// </summary>
    /// <param name="app">The endpoint route builder to add scenario routes to.</param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> instance for chaining.</returns>
    /// <remarks>
    /// Mapped routes:
    /// <list type="bullet">
    ///   <item>
    ///     <term><c>GET /api/scenarios</c></term>
    ///     <description>
    ///       Returns <c>200 OK</c> with a JSON array of <see cref="ScenarioInfoDto"/> objects representing all scenarios
    ///       in <see cref="ScenarioCatalog"/>, including each scenario's <c>Id</c>, <c>Name</c>, <c>Description</c>,
    ///       <c>IsCustom</c> flag, optional <c>ScriptId</c> (from <see cref="ScriptScenarioStore"/>),
    ///       <c>Parameters</c> JSON Schema (<c>JsonObject</c>), and optional <c>Steps</c> list (<see cref="StepInfo"/>,
    ///       <see langword="null"/> when the scenario defines no steps). Takes no route or query parameters.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
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
