using Beesharp.Services;

namespace Beesharp.Api;

/// <summary>
/// REST for the programs that run around the scenarios: preflights (<c>/api/preflights</c>, brain 0042) and shutdown programs
/// (<c>/api/shutdowns</c>, brain 0052). They are the same kind of script, so one mapping serves both.
/// </summary>
public static class ProgramEndpoints
{
    public static IEndpointRouteBuilder MapPreflightEndpoints(this IEndpointRouteBuilder app) => app.MapProgramEndpoints<PreflightStore>("/api/preflights");

    public static IEndpointRouteBuilder MapShutdownEndpoints(this IEndpointRouteBuilder app) => app.MapProgramEndpoints<ShutdownStore>("/api/shutdowns");

    private static IEndpointRouteBuilder MapProgramEndpoints<TStore>(this IEndpointRouteBuilder app, string route) where TStore : ScriptProgramStore
    {
        app.MapGet(route, (TStore store) => store.List());
        app.MapGet(route + "/{id}", (string id, TStore store) =>
            store.Get(id) is { } p ? Results.Ok(p) : Results.NotFound());
        app.MapPost(route, async (UploadScriptRequest body, TStore store, CancellationToken ct) =>
        {
            var result = await store.CompileAndSaveAsync(body.Id, body.FileName, body.Code ?? "", persist: true, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });
        app.MapDelete(route + "/{id}", (string id, TStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());
        app.MapPost(route + "/{id}/test-run", async (string id, TestRunPreflightRequest? body, TStore store, CancellationToken ct) =>
        {
            try
            {
                var result = await store.ExecuteAsync(id, body?.EnvironmentName, timeoutSeconds: body?.TimeoutSeconds ?? 60, ct: ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
        return app;
    }
}
