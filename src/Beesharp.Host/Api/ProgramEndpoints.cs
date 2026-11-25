using Beesharp.Services;

namespace Beesharp.Api;

/// <summary>
/// Maps REST endpoints for lifecycle program scripts that run around load scenarios:
/// preflight scripts (<c>/api/preflights</c>) and shutdown programs (<c>/api/shutdowns</c>).
/// Both kinds share the same script model and endpoint mapping.
/// </summary>
public static class ProgramEndpoints
{
    /// <summary>
    /// Maps the preflight program REST endpoints under <c>/api/preflights</c>, backed by <see cref="PreflightStore"/>.
    /// </summary>
    /// <param name="app">The endpoint route builder to register the preflight routes on.</param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> instance for chaining.</returns>
    /// <remarks>
    /// Registers the following HTTP endpoints:
    /// <list type="bullet">
    /// <item>
    /// <description><c>GET /api/preflights</c> — Lists all registered preflight scripts; returns <c>200 OK</c> with a list of <see cref="PreflightScriptSummary"/>.</description>
    /// </item>
    /// <item>
    /// <description><c>GET /api/preflights/{id}</c> — Gets a single preflight script by route parameter <c>id</c>; returns <c>200 OK</c> with <see cref="PreflightScriptSummary"/>, or <c>404 Not Found</c>.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/preflights</c> — Compiles and persists a preflight script from an <see cref="UploadScriptRequest"/> body (<c>Id</c>, <c>FileName</c>, <c>Code</c>); returns <c>200 OK</c> with <see cref="PreflightCompileResult"/> on success, or <c>400 Bad Request</c> with <see cref="PreflightCompileResult"/> on validation/compilation failure.</description>
    /// </item>
    /// <item>
    /// <description><c>DELETE /api/preflights/{id}</c> — Deletes a script-file preflight by route parameter <c>id</c>; returns <c>204 No Content</c>, or <c>404 Not Found</c> if missing or compiled-in.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/preflights/{id}/test-run</c> — Executes a preflight script by route parameter <c>id</c> with an optional <see cref="TestRunPreflightRequest"/> body (<c>EnvironmentName</c>, <c>TimeoutSeconds</c> defaulting to <c>60</c>); returns <c>200 OK</c> with <see cref="PreflightRunResult"/>, or <c>404 Not Found</c> with <c>{ error }</c> if the script is not found.</description>
    /// </item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapPreflightEndpoints(this IEndpointRouteBuilder app) => app.MapProgramEndpoints<PreflightStore>("/api/preflights");

    /// <summary>
    /// Maps the shutdown program REST endpoints under <c>/api/shutdowns</c>, backed by <see cref="ShutdownStore"/>.
    /// </summary>
    /// <param name="app">The endpoint route builder to register the shutdown routes on.</param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> instance for chaining.</returns>
    /// <remarks>
    /// Registers the following HTTP endpoints:
    /// <list type="bullet">
    /// <item>
    /// <description><c>GET /api/shutdowns</c> — Lists all registered shutdown programs; returns <c>200 OK</c> with a list of <see cref="PreflightScriptSummary"/>.</description>
    /// </item>
    /// <item>
    /// <description><c>GET /api/shutdowns/{id}</c> — Gets a single shutdown program by route parameter <c>id</c>; returns <c>200 OK</c> with <see cref="PreflightScriptSummary"/>, or <c>404 Not Found</c>.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/shutdowns</c> — Compiles and persists a shutdown program from an <see cref="UploadScriptRequest"/> body (<c>Id</c>, <c>FileName</c>, <c>Code</c>); returns <c>200 OK</c> with <see cref="PreflightCompileResult"/> on success, or <c>400 Bad Request</c> with <see cref="PreflightCompileResult"/> on validation/compilation failure.</description>
    /// </item>
    /// <item>
    /// <description><c>DELETE /api/shutdowns/{id}</c> — Deletes a script-file shutdown program by route parameter <c>id</c>; returns <c>204 No Content</c>, or <c>404 Not Found</c> if missing or compiled-in.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/shutdowns/{id}/test-run</c> — Executes a shutdown program by route parameter <c>id</c> with an optional <see cref="TestRunPreflightRequest"/> body (<c>EnvironmentName</c>, <c>TimeoutSeconds</c> defaulting to <c>60</c>); returns <c>200 OK</c> with <see cref="PreflightRunResult"/>, or <c>404 Not Found</c> with <c>{ error }</c> if the script is not found.</description>
    /// </item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapShutdownEndpoints(this IEndpointRouteBuilder app) => app.MapProgramEndpoints<ShutdownStore>("/api/shutdowns");

    private static IEndpointRouteBuilder MapProgramEndpoints<TStore>(this IEndpointRouteBuilder app, string route) where TStore : ScriptProgramStore
    {
        // GET {route}: list all scripts in the store.
        app.MapGet(route, (TStore store) => store.List());
        // GET {route}/{id}: fetch a single script summary, or 404.
        app.MapGet(route + "/{id}", (string id, TStore store) =>
            store.Get(id) is { } p ? Results.Ok(p) : Results.NotFound());
        // POST {route}: compile and persist a script; 200 on success, 400 on diagnostics/validation error.
        app.MapPost(route, async (UploadScriptRequest body, TStore store, CancellationToken ct) =>
        {
            var result = await store.CompileAndSaveAsync(body.Id, body.FileName, body.Code ?? "", persist: true, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });
        // DELETE {route}/{id}: remove a file-backed script; 204 if deleted, 404 if missing or compiled-in.
        app.MapDelete(route + "/{id}", (string id, TStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());
        // POST {route}/{id}/test-run: execute the program with optional environment and timeout (default 60s).
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
