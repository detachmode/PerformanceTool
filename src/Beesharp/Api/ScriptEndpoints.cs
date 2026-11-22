using Beesharp.Services;

namespace Beesharp.Api;

/// <summary>REST for the custom C# scenario scripts (brain 0040).</summary>
public static class ScriptEndpoints
{
    /// <summary>Upload/compile .csx/.cs scripts and test-run a single execution.</summary>
    public static IEndpointRouteBuilder MapScriptEndpoints(this IEndpointRouteBuilder app)
    {
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
        return app;
    }
}
