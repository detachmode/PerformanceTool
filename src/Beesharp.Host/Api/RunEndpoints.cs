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
                var envName = string.IsNullOrWhiteSpace(body?.EnvironmentName) ? workspace.DefaultEnvironment : body.EnvironmentName;
                return Results.Ok(await runs.StartAsync(body?.Plan ?? workspace.GetPlan(), envName, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/runs/stop", (RunManager runs) => runs.StopAsync());
        // Pause holds the run (no new bees, the run time stands still, bees in flight finish); resume goes on; stop ends it (brain 0027).
        app.MapPost("/api/runs/pause", (RunManager runs) => runs.Pause());
        app.MapPost("/api/runs/resume", (RunManager runs) => runs.Resume());
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
        app.MapPost("/api/runs/unload", (RunManager runs) =>
            runs.UnloadStoredRun() ? Results.Ok() : Results.BadRequest(new { error = "Cannot unload while a live run is in progress." }));
        app.MapPost("/api/runs/import", async (IFormFile file, IRunStorage storage, CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No run file was uploaded." });
            try
            {
                await using var stream = file.OpenReadStream();
                var summary = await storage.ImportRunAsync(file.FileName, stream, ct);
                return Results.Ok(summary);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Text.Json.JsonException or InvalidDataException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();
        app.MapGet("/api/runs/{runId}/bees/{sequence:int}", async (string runId, int sequence, IRunStorage storage, CancellationToken ct) =>
            await storage.GetBeeDetailAsync(runId, sequence, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        app.MapGet("/api/runs/{runId}/logs", (
            string runId,
            int? entry,
            int? step,
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
            storage.QueryLogsAsync(runId, LogFilter(entry, step, bee, beeName, outcome, failed, slow, levels, search, offset ?? 0, limit ?? 200), ct));
        // The counts behind the Logs page's filter column (brain 0059): the same filters as the log list.
        app.MapGet("/api/runs/{runId}/logs/facets", (
            string runId,
            int? entry,
            int? step,
            int? bee,
            string? beeName,
            int? outcome,
            bool? failed,
            bool? slow,
            string? levels,
            string? search,
            IRunStorage storage,
            CancellationToken ct) =>
            storage.GetLogFacetsAsync(runId, LogFilter(entry, step, bee, beeName, outcome, failed, slow, levels, search, 0, 1), ct));
        app.MapGet("/api/runs/{runId}/export", async (string runId, string? format, IRunStorage storage, CancellationToken ct) =>
        {
            var export = await storage.ExportRunAsync(runId, format ?? "parquet", ct);
            return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
        });
        app.MapGet("/api/runs/{runId}/calls/endpoints", async (
            string runId,
            int? entry,
            string? scenario,
            int? step,
            bool? byStep,
            string? endpoint,
            bool? slowOnly,
            bool? failedOnly,
            double? minMs,
            string? search,
            RunManager runs,
            IRunStorage storage,
            CancellationToken ct) =>
        {
            if (runs.IsCurrent(runId)) await storage.FlushAsync(ct);
            return Results.Ok(await storage.GetCallEndpointsAsync(
                runId,
                new CallEndpointsQuery(entry, slowOnly ?? false, failedOnly ?? false, minMs, search, step, byStep ?? false, scenario, endpoint),
                ct));
        });
        app.MapGet("/api/runs/{runId}/calls/slowest", async (
            string runId,
            int? limit,
            int? entry,
            string? scenario,
            int? step,
            string? endpoint,
            bool? slowOnly,
            bool? failedOnly,
            double? minMs,
            string? search,
            RunManager runs,
            IRunStorage storage,
            CancellationToken ct) =>
        {
            if (runs.IsCurrent(runId)) await storage.FlushAsync(ct);
            return Results.Ok(await storage.GetSlowestCallsAsync(
                runId,
                new SlowestCallsQuery(limit ?? 20, entry, scenario, step, endpoint, slowOnly ?? false, failedOnly ?? false, minMs, search),
                ct));
        });
        app.MapGet("/api/runs/{runId}/calls/detail", async (
            string runId,
            string? endpoint,
            int? entry,
            string? show,
            double? minMs,
            double? maxMs,
            double? fromSec,
            double? toSec,
            string? sort,
            int? offset,
            int? limit,
            int? maxPoints,
            RunManager runs,
            IRunStorage storage,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return Results.BadRequest(new { error = "endpoint query parameter is required." });
            if (runs.IsCurrent(runId)) await storage.FlushAsync(ct);
            var detail = await storage.GetCallEndpointDetailAsync(
                runId,
                new CallDetailQuery(endpoint, entry, show, minMs, maxMs, fromSec, toSec, sort, offset ?? 0, limit ?? 200, maxPoints ?? 240),
                ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });
        return app;
    }

    private static LogQuery LogFilter(int? entry, int? step, int? bee, string? beeName, int? outcome, bool? failed, bool? slow, string? levels, string? search, int offset, int limit)
    {
        var levelList = string.IsNullOrWhiteSpace(levels)
            ? null
            : levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new LogQuery(entry, bee, beeName, outcome, failed, slow, levelList, search, offset, limit, step);
    }
}
