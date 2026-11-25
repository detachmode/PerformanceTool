using Beesharp.Services;

namespace Beesharp.Api;

/// <summary>
/// REST endpoints for managing custom C# scenario scripts and executing single-bee script test runs (brain 0040).
/// </summary>
/// <remarks>
/// Exposes script listing, retrieval, upload/compilation, deletion, synchronous test runs,
/// and asynchronous polled live test runs under <c>/api/scripts</c>.
/// </remarks>
public static class ScriptEndpoints
{
    /// <summary>
    /// Maps the custom C# scenario script management and test-run HTTP endpoints onto <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The <see cref="IEndpointRouteBuilder"/> to configure.</param>
    /// <returns>The configured <see cref="IEndpointRouteBuilder"/> for chaining.</returns>
    /// <remarks>
    /// Mapped HTTP routes:
    /// <list type="bullet">
    /// <item>
    /// <description><c>GET /api/scripts</c> — Lists all registered custom scenario scripts. Returns <c>200 OK</c> with a JSON array of <see cref="Beesharp.Contracts.ScriptScenarioSummary"/>.</description>
    /// </item>
    /// <item>
    /// <description><c>GET /api/scripts/{id}</c> — Retrieves a single registered script by route parameter <c>id</c>. Returns <c>200 OK</c> with <see cref="Beesharp.Contracts.ScriptScenarioSummary"/>, or <c>404 Not Found</c> if not found.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/scripts</c> — Uploads, compiles, registers, and persists a <c>.csx</c>/<c>.cs</c> scenario script. Accepts either <c>multipart/form-data</c> (optional <c>id</c> form field and a non-empty uploaded script file) or a JSON <see cref="Beesharp.Contracts.UploadScriptRequest"/> (<c>{ id, fileName, code }</c>). Returns <c>200 OK</c> with <see cref="Beesharp.Contracts.ScriptCompileResult"/> on compile success, or <c>400 Bad Request</c> with <see cref="Beesharp.Contracts.ScriptCompileResult"/> (on compilation errors) or <c>{ error }</c> (when the file or JSON body is missing/empty).</description>
    /// </item>
    /// <item>
    /// <description><c>DELETE /api/scripts/{id}</c> — Deletes and unregisters the script identified by route parameter <c>id</c>. Returns <c>204 No Content</c> when deleted, or <c>404 Not Found</c> if not found.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/scripts/{id}/test-run</c> — Executes a single-bee test run synchronously for script <c>id</c>. Accepts an optional JSON <see cref="Beesharp.Contracts.TestRunScriptRequest"/> (<c>{ parameters, environmentName, timeoutSeconds }</c>; <c>timeoutSeconds</c> defaults to <c>30</c>). Returns <c>200 OK</c> with <see cref="Beesharp.Contracts.ScriptTestRunResult"/>, <c>404 Not Found</c> with <c>{ error }</c> if the script or environment is missing, or <c>400 Bad Request</c> with <c>{ error }</c> on invalid arguments or state.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/scripts/{id}/test-run/start</c> — Starts an asynchronous single-bee live test run for script <c>id</c>. Accepts an optional JSON <see cref="Beesharp.Contracts.TestRunScriptRequest"/> (<c>{ parameters, environmentName, timeoutSeconds }</c>; <c>timeoutSeconds</c> defaults to <c>30</c>). Returns <c>200 OK</c> with the initial <see cref="Beesharp.Contracts.LiveTestRunStatusDto"/>, <c>404 Not Found</c> with <c>{ error }</c> if the script or environment is missing, or <c>400 Bad Request</c> with <c>{ error }</c> on invalid arguments or state.</description>
    /// </item>
    /// <item>
    /// <description><c>GET /api/scripts/test-runs/{testRunId}</c> — Polls the current state of a live test run by route parameter <c>testRunId</c>. Returns <c>200 OK</c> with <see cref="Beesharp.Contracts.LiveTestRunStatusDto"/>, or <c>404 Not Found</c> if unknown.</description>
    /// </item>
    /// <item>
    /// <description><c>POST /api/scripts/test-runs/{testRunId}/stop</c> — Requests cancellation of an active live test run by route parameter <c>testRunId</c>. Returns <c>200 OK</c> if found, or <c>404 Not Found</c> if unknown.</description>
    /// </item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapScriptEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/scripts -> 200 OK (IReadOnlyList<ScriptScenarioSummary>)
        app.MapGet("/api/scripts", (ScriptScenarioStore store) => store.List());

        // GET /api/scripts/{id} -> 200 OK (ScriptScenarioSummary) | 404 Not Found
        app.MapGet("/api/scripts/{id}", (string id, ScriptScenarioStore store) =>
            store.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());

        // POST /api/scripts -> 200 OK (ScriptCompileResult) | 400 Bad Request (ScriptCompileResult | { error })
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

        // DELETE /api/scripts/{id} -> 204 No Content | 404 Not Found
        app.MapDelete("/api/scripts/{id}", (string id, ScriptScenarioStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());

        // POST /api/scripts/{id}/test-run -> 200 OK (ScriptTestRunResult) | 400 Bad Request | 404 Not Found
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

        // POST /api/scripts/{id}/test-run/start -> 200 OK (LiveTestRunStatusDto) | 400 Bad Request | 404 Not Found
        app.MapPost("/api/scripts/{id}/test-run/start", async (string id, TestRunScriptRequest? body, ScriptScenarioStore store, CancellationToken ct) =>
        {
            try
            {
                var started = await store.StartLiveTestRunAsync(id, body?.Parameters, body?.EnvironmentName, body?.TimeoutSeconds ?? 30, ct);
                return Results.Ok(started);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /api/scripts/test-runs/{testRunId} -> 200 OK (LiveTestRunStatusDto) | 404 Not Found
        app.MapGet("/api/scripts/test-runs/{testRunId}", (string testRunId, ScriptScenarioStore store) =>
            store.GetLiveTestRun(testRunId) is { } snap ? Results.Ok(snap) : Results.NotFound());

        // POST /api/scripts/test-runs/{testRunId}/stop -> 200 OK | 404 Not Found
        app.MapPost("/api/scripts/test-runs/{testRunId}/stop", (string testRunId, ScriptScenarioStore store) =>
            store.StopLiveTestRun(testRunId) ? Results.Ok() : Results.NotFound());

        return app;
    }
}
