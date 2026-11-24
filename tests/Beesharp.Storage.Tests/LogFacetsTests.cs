using Beesharp.Services;
using Beesharp.Services.Storage;

namespace Beesharp.Storage.Tests;

/// <summary>The step filter and the counts behind the Logs page's filter column (brain 0059), the same for both providers.</summary>
public sealed class LogFacetsTests
{
    public static TheoryData<string> Providers => new() { "memory", "duckdb" };

    private static async Task WithRunAsync(string provider, Func<IRunStorage, Task> test)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"beesharp-facets-{Guid.NewGuid():N}.duckdb");
        await using IRunStorage storage = provider == "memory" ? new InMemoryRunStorage() : new DuckDbRunStorage(dbPath);
        try
        {
            storage.EnqueueRunStarted("r", DateTimeOffset.UtcNow, new LoadPlan { DurationSeconds = 10 });
            // entry 0, bee B1: step 0 info, step 1 error (twice) and warning; entry 0, bee B2: step 1 error; entry 1, bee B3: info
            storage.EnqueueBeeCompleted("r", new ExecutionResult(0, 0, 0, 100, 500, 1, "boom", Logs:
                [new RecordedLog(0.1, "Information", "log in", Step: 0), new RecordedLog(0.2, "Error", "GET /a 503", Step: 1), new RecordedLog(0.3, "Error", "GET /b 503", Step: 1), new RecordedLog(0.4, "Warning", "slow", Step: 1)]), 2, 0);
            storage.EnqueueBeeCompleted("r", new ExecutionResult(1, 0, 0, 100, 500, 1, "boom", Logs: [new RecordedLog(0.5, "Error", "GET /a 503", Step: 1)]), 2, 0);
            storage.EnqueueBeeCompleted("r", new ExecutionResult(2, 1, 0, 100, 200, 1, null, Logs: [new RecordedLog(0.6, "Information", "hello", Step: 0)]), 0, 0);
            await storage.FlushAsync();
            await test(storage);
        }
        finally
        {
            foreach (var f in new[] { dbPath, dbPath + ".wal" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Theory, MemberData(nameof(Providers))]
    public Task The_step_filter_keeps_only_the_lines_of_that_step(string provider) => WithRunAsync(provider, async storage =>
    {
        var page = await storage.QueryLogsAsync("r", new LogQuery(Entry: 0, Step: 1));
        Assert.Equal(4, page.TotalCount);
        Assert.All(page.Items, l => Assert.Equal(1, l.Step));
        Assert.Equal(1, (await storage.QueryLogsAsync("r", new LogQuery(Entry: 0, Step: 0))).TotalCount);
    });

    [Theory, MemberData(nameof(Providers))]
    public Task Counts_per_level_ignore_the_level_filter_but_not_the_others(string provider) => WithRunAsync(provider, async storage =>
    {
        var all = await storage.GetLogFacetsAsync("r", new LogQuery());
        Assert.Equal(6, all.Total);
        Assert.Equal(3, all.Levels["Error"]);
        Assert.Equal(2, all.Levels["Information"]);
        Assert.Equal(1, all.Levels["Warning"]);

        var errorsOfEntry0 = await storage.GetLogFacetsAsync("r", new LogQuery(Entry: 0, Levels: ["Error"]));
        Assert.Equal(3, errorsOfEntry0.Total);          // the whole filter
        Assert.Equal(5, errorsOfEntry0.Levels.Values.Sum()); // every level of entry 0, whatever the level filter says
        Assert.Equal(1, errorsOfEntry0.Levels["Information"]);
    });

    [Theory, MemberData(nameof(Providers))]
    public Task Counts_per_entry_ignore_the_entry_filter_and_follow_the_search(string provider) => WithRunAsync(provider, async storage =>
    {
        var f = await storage.GetLogFacetsAsync("r", new LogQuery(Entry: 1));
        Assert.Equal([5L, 1L], f.Entries);
        Assert.Equal(1, f.Total);

        var search = await storage.GetLogFacetsAsync("r", new LogQuery(Search: "503"));
        Assert.Equal([3L], search.Entries);
    });

    [Theory, MemberData(nameof(Providers))]
    public Task Steps_of_the_chosen_entry_count_lines_and_errors(string provider) => WithRunAsync(provider, async storage =>
    {
        var none = await storage.GetLogFacetsAsync("r", new LogQuery());
        Assert.Empty(none.Steps);

        var f = await storage.GetLogFacetsAsync("r", new LogQuery(Entry: 0, Step: 0, Levels: ["Information"]));
        Assert.Equal([new LogStepCount(0, 1, 0), new LogStepCount(1, 4, 3)], f.Steps);
        Assert.Equal(1, f.Total); // the step and level filters still apply to the total
    });

    [Theory, MemberData(nameof(Providers))]
    public Task A_bee_and_the_failed_flag_narrow_every_count(string provider) => WithRunAsync(provider, async storage =>
    {
        var f = await storage.GetLogFacetsAsync("r", new LogQuery(Bee: 1, Failed: true));
        Assert.Equal(1, f.Total);
        Assert.Equal(1, f.Levels["Error"]);
        Assert.Equal([1L], f.Entries);
    });

    [Theory, MemberData(nameof(Providers))]
    public Task Preflight_and_shutdown_programs_persist_and_appear_in_log_queries_and_facets(string provider) => WithRunAsync(provider, async storage =>
    {
        await storage.SaveProgramRunAsync("r", "preflight", new StoredProgramRunDto(
            "Succeeded", "WarmupPreflight.cs", null,
            ["[12:00:00.000] [INF] Priming cache", "[12:00:00.100] [WRN] Slow DNS"]));
        await storage.SaveProgramRunAsync("r", "shutdown", new StoredProgramRunDto(
            "Failed", "CleanupShutdown.cs", "cleanup error",
            ["[12:00:10.000] [ERR] Could not delete tenant"]));

        var run = await storage.GetRunAsync("r");
        Assert.NotNull(run);
        Assert.Equal("Succeeded", run!.Preflight?.State);
        Assert.Equal("WarmupPreflight.cs", run.Preflight?.Name);
        Assert.Equal(2, run.Preflight?.Logs.Count);
        Assert.Equal("Failed", run.Shutdown?.State);
        Assert.Equal("CleanupShutdown.cs", run.Shutdown?.Name);
        Assert.Equal("cleanup error", run.Shutdown?.Error);
        Assert.Single(run.Shutdown!.Logs);

        var facets = await storage.GetLogFacetsAsync("r", new LogQuery());
        Assert.Equal(9, facets.Total);
        Assert.Equal(2, facets.Preflight);
        Assert.Equal(1, facets.Shutdown);
        Assert.Equal([5L, 1L], facets.Entries);

        var pfPage = await storage.QueryLogsAsync("r", new LogQuery(Entry: ProgramLogEntry.Preflight));
        Assert.Equal(2, pfPage.TotalCount);
        Assert.Equal("Preflight", pfPage.Items[0].BeeName);
        Assert.Equal("Information", pfPage.Items[0].Level);
        Assert.Equal("Priming cache", pfPage.Items[0].Message);
        Assert.Equal("Warning", pfPage.Items[1].Level);
        Assert.Equal("Slow DNS", pfPage.Items[1].Message);

        var sdPage = await storage.QueryLogsAsync("r", new LogQuery(Entry: ProgramLogEntry.Shutdown));
        Assert.Equal(1, sdPage.TotalCount);
        Assert.Equal("Shutdown", sdPage.Items[0].BeeName);
        Assert.Equal("Error", sdPage.Items[0].Level);
        Assert.Equal("Could not delete tenant", sdPage.Items[0].Message);

        // Filtering by preflight leaves steps empty (no scenario steps on program runs)
        var pfFacets = await storage.GetLogFacetsAsync("r", new LogQuery(Entry: ProgramLogEntry.Preflight));
        Assert.Equal(2, pfFacets.Total);
        Assert.Empty(pfFacets.Steps);
    });
}
