using Beesharp.Services;

namespace Beesharp.Api;

/// <summary>
/// Maps REST endpoints for the server-owned Live <see cref="LoadPlan"/> workspace and saved presets.
/// </summary>
public static class PlanEndpoints
{
    /// <summary>
    /// Registers the <c>/api/plan</c> and <c>/api/presets</c> HTTP endpoints on <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The endpoint route builder to register the plan and preset routes on.</param>
    /// <returns>The same <paramref name="app"/> instance for fluent chaining.</returns>
    /// <remarks>
    /// Mapped routes:
    /// <list type="bullet">
    /// <item>
    /// <description><c>GET /api/plan</c> — Optional query parameter <c>knownVersion</c> (<see cref="long"/>). Returns <c>200 OK</c> with <see cref="PlanWorkspaceState"/>, or <c>204 No Content</c> when <c>knownVersion</c> matches <see cref="PlanWorkspace.Version"/>.</description>
    /// </item>
    /// <item>
    /// <description><c>PUT /api/plan</c> — Request body <see cref="LoadPlan"/> (last writer wins). Replaces the workspace plan and returns <c>200 OK</c> with <see cref="PlanWorkspaceState"/>.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/plan/entries</c> — Request body <see cref="AddEntryRequest"/>. Appends a scenario entry with default parameters and returns <c>200 OK</c> with <see cref="PlanWorkspaceState"/>, or <c>400 Bad Request</c> with <c>{ error }</c> when the scenario is unknown or invalid.</description>
    /// </item>
    /// <item>
    /// <description><c>DELETE /api/plan/entries/{index:int}</c> — Route parameter <c>index</c> (0-based <see cref="int"/>). Removes the scenario entry at that index and returns <c>200 OK</c> with <see cref="PlanWorkspaceState"/>, or <c>404 Not Found</c> with <c>{ error }</c> when the index is out of range.</description>
    /// </item>
    /// <item>
    /// <description><c>GET /api/presets</c> — Lists all available code and disk presets. Returns <c>200 OK</c> with <see cref="IReadOnlyList{T}"/> of <see cref="Preset"/>.</description>
    /// </item>
    /// <item>
    /// <description><c>PUT /api/presets/{name}</c> — Route parameter <c>name</c> (<see cref="string"/>); optional request body <see cref="LoadPlan"/> (defaults to the current workspace plan when omitted). Saves the preset, selects it as current, and returns <c>200 OK</c> with <see cref="PlanWorkspaceState"/>, or <c>400 Bad Request</c> with <c>{ error }</c> when <c>name</c> is blank or invalid.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/presets/{name}/load</c> — Route parameter <c>name</c> (<see cref="string"/>). Loads the named preset into the workspace and returns <c>200 OK</c> with <see cref="PlanWorkspaceState"/>, or <c>404 Not Found</c> when no matching preset exists.</description>
    /// </item>
    /// <item>
    /// <description><c>DELETE /api/presets/{name}</c> — Route parameter <c>name</c> (<see cref="string"/>). Deletes the named disk preset and returns <c>204 No Content</c>, or <c>404 Not Found</c> when no matching disk preset exists.</description>
    /// </item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        // The plan being edited. With ?knownVersion=N the answer is 204 when nothing changed (cheap polling).
        app.MapGet("/api/plan", (long? knownVersion, PlanWorkspace plan) =>
            knownVersion is { } known && known == plan.Version ? Results.NoContent() : Results.Ok(plan.GetState()));
        // Last writer wins: the editor pushes the whole plan.
        app.MapPut("/api/plan", (LoadPlan body, PlanWorkspace plan) => Results.Ok(plan.SetPlan(body)));
        // Appends a scenario entry with default parameters by scenario ID.
        app.MapPost("/api/plan/entries", (AddEntryRequest body, PlanWorkspace plan) =>
        {
            try { plan.AddEntry(body.ScenarioId); return Results.Ok(plan.GetState()); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        // Removes the scenario entry at the given 0-based index.
        app.MapDelete("/api/plan/entries/{index:int}", (int index, PlanWorkspace plan) =>
        {
            try { return Results.Ok(plan.RemoveEntry(index)); }
            catch (ArgumentOutOfRangeException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        // Lists all merged code and disk presets ordered by name.
        app.MapGet("/api/presets", (PlanWorkspace plan, CancellationToken ct) => plan.ListPresetsAsync(ct));
        // Saves the plan in the body (or the current plan when there is none) as a preset and makes it the current one.
        app.MapPut("/api/presets/{name}", async (string name, LoadPlan? body, PlanWorkspace plan, CancellationToken ct) =>
        {
            try { return Results.Ok(await plan.SavePresetAsync(name, body, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        // Loads the named preset into the workspace.
        app.MapPost("/api/presets/{name}/load", async (string name, PlanWorkspace plan, CancellationToken ct) =>
            await plan.LoadPresetAsync(name, ct) is { } state ? Results.Ok(state) : Results.NotFound());
        // Deletes the named disk preset.
        app.MapDelete("/api/presets/{name}", async (string name, PlanWorkspace plan, CancellationToken ct) =>
            await plan.DeletePresetAsync(name, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
