using PerformanceTool.Services;

namespace PerformanceTool.Api;

/// <summary>REST for the server-owned Live plan and the presets (brain 0047/0050).</summary>
public static class PlanEndpoints
{
    public sealed record AddEntryRequest(string ScenarioId);

    public static IEndpointRouteBuilder MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        // The plan being edited. With ?knownVersion=N the answer is 204 when nothing changed (cheap polling).
        app.MapGet("/api/plan", (long? knownVersion, PlanWorkspace plan) =>
            knownVersion is { } known && known == plan.Version ? Results.NoContent() : Results.Ok(plan.GetState()));
        // Last writer wins: the editor pushes the whole plan.
        app.MapPut("/api/plan", (LoadPlan body, PlanWorkspace plan) => Results.Ok(plan.SetPlan(body)));
        app.MapPost("/api/plan/entries", (AddEntryRequest body, PlanWorkspace plan) =>
        {
            try { plan.AddEntry(body.ScenarioId); return Results.Ok(plan.GetState()); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapDelete("/api/plan/entries/{index:int}", (int index, PlanWorkspace plan) =>
        {
            try { return Results.Ok(plan.RemoveEntry(index)); }
            catch (ArgumentOutOfRangeException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapGet("/api/presets", (PlanWorkspace plan, CancellationToken ct) => plan.ListPresetsAsync(ct));
        // Saves the plan in the body (or the current plan when there is none) as a preset and makes it the current one.
        app.MapPut("/api/presets/{name}", async (string name, LoadPlan? body, PlanWorkspace plan, CancellationToken ct) =>
        {
            try { return Results.Ok(await plan.SavePresetAsync(name, body, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/presets/{name}/load", async (string name, PlanWorkspace plan, CancellationToken ct) =>
            await plan.LoadPresetAsync(name, ct) is { } state ? Results.Ok(state) : Results.NotFound());
        app.MapDelete("/api/presets/{name}", async (string name, PlanWorkspace plan, CancellationToken ct) =>
            await plan.DeletePresetAsync(name, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
