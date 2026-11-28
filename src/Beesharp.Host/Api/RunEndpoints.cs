using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;

namespace Beesharp.Api;

/// <summary>
/// REST endpoints for runs (brain 0045): start, stop, pause, resume, live status polling, runtime data,
/// and stored-run operations (history, load/unload, import/export, bee details, logs, and call analytics).
/// Read-only queries by run id for the assistant's cards live in <see cref="RunQueryEndpoints"/>.
/// </summary>
public static class RunEndpoints
{
    /// <summary>
    /// Maps all <c>/api/runs</c> HTTP endpoints onto <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The endpoint route builder to register the run routes on.</param>
    /// <returns>The same <paramref name="app"/> instance for chaining.</returns>
    /// <remarks>
    /// Mapped routes (all indices are 0-based):
    /// <list type="bullet">
    /// <item><description><c>GET /api/runs?limit=50</c> — Lists recent stored runs; returns <c>200 OK</c> with <see cref="IReadOnlyList{T}"/> of <see cref="StoredRunSummary"/>.</description></item>
    /// <item><description><c>GET /api/runs/current?afterSeq=-1&amp;afterIndex=&amp;runId=&amp;maxBees=2000</c> — Incremental poll of the active or loaded run; returns <c>200 OK</c> with <see cref="LiveRunStatusDto"/>.</description></item>
    /// <item><description><c>GET /api/runs/current/runtime</c> — Runtime data (run bag and entry bags) of the active or loaded run; returns <c>200 OK</c> with <see cref="Beesharp.Services.Runtime.RuntimeDataSnapshot"/> or <c>204 No Content</c> when none is available.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/runtime</c> — Runtime data for <c>runId</c> (live if active, otherwise persisted final values); returns <c>200 OK</c> with <see cref="Beesharp.Services.Runtime.RuntimeDataSnapshot"/> or <c>404 Not Found</c>.</description></item>
    /// <item><description><c>GET /api/runs/live-logs?afterId=0&amp;limit=500&amp;sequence=&amp;entry=</c> — Incremental live log lines from in-flight bees and programs; returns <c>200 OK</c> with <see cref="IReadOnlyList{T}"/> of <see cref="LiveLogEntryDto"/>.</description></item>
    /// <item><description><c>POST /api/runs/start</c> — Optional JSON body <see cref="StartRunRequest"/> (<c>Plan</c>, <c>EnvironmentName</c>; defaults to the server Live plan and default environment). Starts a new run; returns <c>200 OK</c> with <see cref="LiveRunStatusDto"/>, or <c>400 Bad Request</c> with <c>{ error }</c> on invalid plan/scripts or if a run is already active.</description></item>
    /// <item><description><c>POST /api/runs/stop</c> — Cancels the active run (the shutdown program still runs); returns <c>200 OK</c> with <see cref="LiveRunStatusDto"/>.</description></item>
    /// <item><description><c>POST /api/runs/pause</c> — Pauses a running run (in-flight bees finish, no new bees start); returns <c>200 OK</c> with <see cref="LiveRunStatusDto"/>.</description></item>
    /// <item><description><c>POST /api/runs/resume</c> — Resumes a paused run; returns <c>200 OK</c> with <see cref="LiveRunStatusDto"/>.</description></item>
    /// <item><description><c>POST /api/runs/{runId}/load</c> — Loads a stored run into the active view; returns <c>200 OK</c> with <see cref="LiveRunStatusDto"/>, <c>404 Not Found</c> if missing, or <c>400 Bad Request</c> with <c>{ error }</c> while a live run is active.</description></item>
    /// <item><description><c>POST /api/runs/unload</c> — Clears a loaded historical run back to idle; returns <c>200 OK</c>, or <c>400 Bad Request</c> with <c>{ error }</c> while a live run is active.</description></item>
    /// <item><description><c>POST /api/runs/import</c> — <c>multipart/form-data</c> upload with <c>file</c> (<c>.zip</c> Parquet bundle, <c>.duckdb</c>, or <c>.json</c>; antiforgery disabled); returns <c>200 OK</c> with <see cref="StoredRunSummary"/>, or <c>400 Bad Request</c> with <c>{ error }</c> when missing, empty, or invalid.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/bees/{sequence:int}</c> — Full detail (calls and logs) of one bee in <c>runId</c>; returns <c>200 OK</c> with <see cref="StoredBeeDetail"/> or <c>404 Not Found</c>.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/logs?entry=&amp;step=&amp;bee=&amp;beeName=&amp;outcome=&amp;failed=&amp;slow=&amp;levels=&amp;search=&amp;offset=0&amp;limit=200</c> — Paged filtered log lines (<c>levels</c> is comma-separated); returns <c>200 OK</c> with <see cref="LogPageResult"/>.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/logs/facets?entry=&amp;step=&amp;bee=&amp;beeName=&amp;outcome=&amp;failed=&amp;slow=&amp;levels=&amp;search=</c> — Filter facet counts matching the same filter parameters as <c>/logs</c>; returns <c>200 OK</c> with <see cref="LogFacets"/>.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/export?format=parquet</c> — Exports a run (<c>parquet</c>, <c>duckdb</c>, <c>json</c>, or <c>csv</c>); returns <c>200 OK</c> file download or <c>404 Not Found</c>.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/calls/endpoints?entry=&amp;scenario=&amp;step=&amp;byStep=false&amp;endpoint=&amp;slowOnly=false&amp;failedOnly=false&amp;minMs=&amp;search=</c> — Aggregated call endpoint metrics (flushes pending writes if <c>runId</c> is current); returns <c>200 OK</c> with <see cref="CallEndpointsResult"/>.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/calls/slowest?limit=20&amp;entry=&amp;scenario=&amp;step=&amp;endpoint=&amp;slowOnly=false&amp;failedOnly=false&amp;minMs=&amp;search=</c> — Top slowest calls (flushes pending writes if <c>runId</c> is current); returns <c>200 OK</c> with <see cref="SlowestCallsResult"/>.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/traces/{key}?call=&amp;maxSpans=50</c> — One bee's distributed trace (brain 0065) as a flat, bounded span list; <c>key</c> is a 1-based bee (<c>5</c> or <c>B5</c>) or a trace ID, <c>call</c> optionally a 1-based call number of the bee; returns <c>200 OK</c> with <see cref="TraceViewDto"/> (<c>found: false</c> with the reason when the trace is not stored), <c>400 Bad Request</c> with <c>{ error }</c> for a bad key or call, or <c>404 Not Found</c> for an unknown run, bee or trace ID.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/hotspots?endpoint=&amp;slowOnly=false&amp;limit=10</c> — Where the time of the slow calls went, per endpoint (brain 0065); returns <c>200 OK</c> with <see cref="HotspotsResultDto"/> or <c>404 Not Found</c>.</description></item>
    /// <item><description><c>GET /api/runs/{runId}/calls/detail?endpoint=&amp;entry=&amp;show=&amp;minMs=&amp;maxMs=&amp;fromSec=&amp;toSec=&amp;sort=&amp;offset=0&amp;limit=200&amp;maxPoints=240</c> — Timeline, histogram, and paged samples for the required <c>endpoint</c> query param (flushes pending writes if <c>runId</c> is current); returns <c>200 OK</c> with <see cref="CallEndpointDetailDto"/>, <c>400 Bad Request</c> with <c>{ error }</c> when <c>endpoint</c> is missing, or <c>404 Not Found</c>.</description></item>
    /// </list>
    /// </remarks>
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
        app.MapGet("/api/runs/{runId}/sources", async (
            string runId,
            IRunStorage storage,
            ScenarioCatalog catalog,
            ScriptScenarioStore scripts,
            PreflightStore preflights,
            ShutdownStore shutdowns,
            CancellationToken ct) =>
        {
            var stored = await storage.GetRunSourcesAsync(runId, ct);
            var annotated = stored.Select(s =>
            {
                var currentSha = CompiledSourceResolver.ResolveCurrentSha256(s.Kind, s.TargetId, catalog, scripts, preflights, shutdowns);
                bool? differs = currentSha is null ? null : !string.Equals(currentSha, s.Sha256, StringComparison.OrdinalIgnoreCase);
                return s with { DiffersFromCurrent = differs };
            }).ToList();
            return Results.Ok(annotated);
        });
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
        // Distributed traces fetched into the run (brain 0065). A trace that is not stored is a 200 with found=false and the reason, so the client can print why.
        app.MapGet("/api/runs/{runId}/traces/{key}", async (
            string runId,
            string key,
            int? call,
            int? maxSpans,
            RunManager runs,
            IRunStorage storage,
            CancellationToken ct) =>
        {
            if (runs.IsCurrent(runId)) await storage.FlushAsync(ct);
            try
            {
                return await storage.GetTraceViewAsync(runId, new TraceViewQuery(key, call, Math.Clamp(maxSpans ?? 50, 1, 5000)), ct) is { } view
                    ? Results.Ok(view)
                    : Results.NotFound();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapGet("/api/runs/{runId}/hotspots", async (
            string runId,
            string? endpoint,
            bool? slowOnly,
            int? limit,
            RunManager runs,
            IRunStorage storage,
            CancellationToken ct) =>
        {
            if (runs.IsCurrent(runId)) await storage.FlushAsync(ct);
            return await storage.GetHotspotsAsync(runId, new HotspotsQuery(endpoint, slowOnly ?? false, Math.Clamp(limit ?? 10, 1, 1000)), ct) is { } result
                ? Results.Ok(result)
                : Results.NotFound();
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
