using Beesharp.Ai;

namespace Beesharp.Api;

/// <summary>
/// Maps read-only HTTP query endpoints for retrieving run summaries and compact bee listings by run identifier
/// (shared with the assistant's <see cref="IRunHistoryPort"/> so UI cards and the assistant always observe the same data; brain 0049).
/// All entry, step, and bee sequence indexes are 0-based, matching the rest of <c>/api/runs</c>.
/// </summary>
public static class RunQueryEndpoints
{
    /// <summary>
    /// Maximum number of bee records that can be requested or returned in a single <c>/api/runs/{runId}/bees</c> call (<c>5000</c>).
    /// </summary>
    public const int MaxBeesPerRequest = 5000;

    /// <summary>
    /// Maps the read-only run query endpoints onto <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The <see cref="IEndpointRouteBuilder"/> to register the endpoints on.</param>
    /// <returns>The same <paramref name="app"/> instance for call chaining.</returns>
    /// <remarks>
    /// Mapped routes:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>GET /api/runs/{runId}/summary</c> — Returns summary figures for a live or stored run (<see cref="RunInfo"/>).
    /// Route parameter: <c>runId</c>.
    /// Status codes: <c>200 OK</c> with <see cref="RunInfo"/>; <c>404 Not Found</c> if the run does not exist.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>GET /api/runs/{runId}/bees</c> — Returns a <see cref="BeePage"/> of compact bee records for a run.
    /// Route parameter: <c>runId</c>.
    /// Query parameters:
    /// <c>sequences</c> (optional comma-separated 0-based bee sequence numbers, up to <see cref="MaxBeesPerRequest"/>; when provided, returns those matching bees and ignores the other filters/paging);
    /// <c>entry</c> (optional 0-based scenario entry index);
    /// <c>outcome</c> (optional case-insensitive filter: <c>failed</c>, <c>slow</c>, <c>ok</c>, or any other/omitted value for all);
    /// <c>failedStep</c> (optional 0-based failed step index);
    /// <c>offset</c> (optional 0-based page offset, default <c>0</c>, clamped to <c>&gt;= 0</c>);
    /// <c>limit</c> (optional page size, default <c>500</c>, clamped to <c>1..5000</c>).
    /// When paging without <c>sequences</c>, bees are ordered failed first, then slow, then ok, and by start time within each group.
    /// Status codes: <c>200 OK</c> with <see cref="BeePage"/>; <c>400 Bad Request</c> (<c>{ "error": "sequences must be a comma separated list of numbers." }</c>) on invalid <c>sequences</c>; <c>404 Not Found</c> if the run does not exist.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapRunQueryEndpoints(this IEndpointRouteBuilder app)
    {
        // Return summary figures for a live or stored run.
        app.MapGet("/api/runs/{runId}/summary", async (string runId, IRunHistoryPort history, CancellationToken ct) =>
            await history.GetRunAsync(runId, ct) is { } run ? Results.Ok(run) : Results.NotFound());

        // ?sequences=1,2,3 returns exactly those bees; otherwise the bees of the run (optionally one entry, one outcome and/or failedStep), failed first, paged.
        app.MapGet("/api/runs/{runId}/bees", async (string runId, string? sequences, int? entry, string? outcome, int? failedStep, int? offset, int? limit, IRunHistoryPort history, CancellationToken ct) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(sequences))
                {
                    var wanted = sequences.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).Take(MaxBeesPerRequest).ToList();
                    var found = await history.GetBeesAsync(runId, wanted, ct);
                    return Results.Ok(new BeePage(found.Count, found));
                }
                var filter = outcome?.ToLowerInvariant() switch
                {
                    "failed" => BeeOutcomeFilter.Failed,
                    "slow" => BeeOutcomeFilter.Slow,
                    "ok" => BeeOutcomeFilter.Ok,
                    _ => BeeOutcomeFilter.All,
                };
                return Results.Ok(await history.ListBeesAsync(runId, entry, filter, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 500, 1, MaxBeesPerRequest), ct, failedStep));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (FormatException) { return Results.BadRequest(new { error = "sequences must be a comma separated list of numbers." }); }
        });

        return app;
    }
}
