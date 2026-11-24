using Beesharp.Ai;

namespace Beesharp.Api;

/// <summary>
/// Read-only run queries by run id (the assistant's cards re-query live data with them, brain 0049): the figures of a run and its compact bees.
/// They answer from the same port the assistant uses, so a card and the agent always see the same data. Indexes are 0-based, like the rest of <c>/api/runs</c>.
/// </summary>
public static class RunQueryEndpoints
{
    public const int MaxBeesPerRequest = 5000;

    public static IEndpointRouteBuilder MapRunQueryEndpoints(this IEndpointRouteBuilder app)
    {
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
