using Beesharp.Services;
using Beesharp.Services.Storage;

namespace Beesharp.Api;

/// <summary>REST for runs (brain 0045): start/stop, live status, runtime data, and the stored runs (history, bees, logs, export). Read-only queries by run id for the assistant's cards are in <see cref="RunQueryEndpoints"/>.</summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runs", (int? limit, IRunStorage storage, CancellationToken ct) =>
            storage.ListRunsAsync(limit ?? 50, ct));
        app.MapGet("/api/runs/current", (int? afterSeq, int? afterIndex, string? runId, int? maxBees, RunManager runs) =>
            runs.GetCurrent(afterSeq ?? -1, maxBees ?? 2000, afterIndex, runId));
        // Runtime data (brain 0052): the run bag and the entry bags of the active run (live), or the final values of a stored run.
        app.MapGet("/api/runs/current/runtime", (RunManager runs) =>
            runs.GetCurrentRuntime() is { } rt ? Results.Ok(rt) : Results.NoContent());
        app.MapGet("/api/runs/{runId}/runtime", async (string runId, RunManager runs, CancellationToken ct) =>
            await runs.GetRuntimeAsync(runId, ct) is { } rt ? Results.Ok(rt) : Results.NotFound());
        app.MapGet("/api/runs/live-logs", (long? afterId, int? limit, int? sequence, int? entry, RunManager runs) =>
            runs.GetLiveLogs(afterId ?? 0, limit ?? 500, sequence, entry));
        app.MapPost("/api/runs/start", async (StartRunRequest? body, RunManager runs, PlanWorkspace workspace, CancellationToken ct) =>
        {
            try
            {
                // No plan in the body = start the server's Live plan. A plan in the body becomes the Live plan.
                if (body?.Plan is { } given) workspace.SetPlan(given);
                return Results.Ok(await runs.StartAsync(body?.Plan ?? workspace.GetPlan(), body?.EnvironmentName, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/runs/stop", (RunManager runs) => runs.StopAsync());
        app.MapPost("/api/runs/{runId}/load", async (string runId, RunManager runs, CancellationToken ct) =>
        {
            try
            {
                return await runs.LoadStoredRunAsync(runId, ct) is { } loaded ? Results.Ok(loaded) : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapGet("/api/runs/{runId}/bees/{sequence:int}", async (string runId, int sequence, IRunStorage storage, CancellationToken ct) =>
            await storage.GetBeeDetailAsync(runId, sequence, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        app.MapGet("/api/runs/{runId}/logs", (
            string runId,
            int? entry,
            int? bee,
            string? beeName,
            int? outcome,
            bool? failed,
            bool? slow,
            string? levels,
            string? search,
            int? offset,
            int? limit,
            IRunStorage storage,
            CancellationToken ct) =>
        {
            var levelList = string.IsNullOrWhiteSpace(levels)
                ? null
                : levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return storage.QueryLogsAsync(
                runId,
                new LogQuery(entry, bee, beeName, outcome, failed, slow, levelList, search, offset ?? 0, limit ?? 200),
                ct);
        });
        app.MapGet("/api/runs/{runId}/export", async (string runId, string? format, IRunStorage storage, CancellationToken ct) =>
        {
            var export = await storage.ExportRunAsync(runId, format ?? "parquet", ct);
            return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
        });
        return app;
    }
}
