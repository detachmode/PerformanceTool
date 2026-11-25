using Beesharp.Client.Data;
using Beesharp.Client.Runs;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>
/// Stories for <see cref="FakeRunSource"/>: live simulation start/pause/resume/stop,
/// debug URL overrides (<c>?burst=</c>, <c>?stepcount=</c>), preset management, and lazy log generation
/// across normal, slow, HTTP 500 retry, timeout, skipped and canceled calls.
/// </summary>
public sealed class FakeRunSourceLifecycleTests
{
    private static ScenarioCatalog BuiltInCatalog() =>
        new([new HttpGetScenario(), new HttpPostJsonScenario(), new CustomRequestScenario(), new UserJourneyScenario()]);

    [Fact]
    public async Task Live_fake_simulation_supports_start_pause_resume_and_stop_with_shutdown_output()
    {
        using var source = new FakeRunSource(BuiltInCatalog(), simulateSeconds: 5);
        source.Plan.DurationSeconds = 30;
        var changedCount = 0;
        source.Changed += () => changedCount++;

        // Start a live simulated run and wait for at least one tick to advance elapsed time.
        source.Start();
        Assert.True(source.IsRunning);
        Assert.Equal(FakeRunState.Running, source.Current.State);
        Assert.NotNull(source.Current.Preflight);

        for (var i = 0; i < 60 && source.Current.Elapsed <= 0; i++)
            await Task.Delay(25);
        Assert.True(source.Current.Elapsed > 0);

        // Pause freezes the run clock while allowing in-flight bees to finish (brain 0027).
        source.Pause();
        Assert.Equal(FakeRunState.Paused, source.Current.State);
        Assert.True(source.IsRunning);
        await Task.Delay(60); // let any in-flight tick observe the paused state
        var elapsedAtPause = source.Current.Elapsed;
        await Task.Delay(120);
        Assert.Equal(elapsedAtPause, source.Current.Elapsed);

        // Resume continues the run; Stop finishes it cleanly and runs the fake shutdown program.
        source.Resume();
        Assert.Equal(FakeRunState.Running, source.Current.State);

        source.Stop();
        Assert.False(source.IsRunning);
        Assert.Equal(FakeRunState.Finished, source.Current.State);
        Assert.NotNull(source.Current.Shutdown);
        Assert.True(changedCount >= 4);
    }

    [Fact]
    public void Debug_burst_and_stepcount_modes_scale_burst_entries_and_generate_synthetic_stepped_entries()
    {
        using var source = new FakeRunSource(BuiltInCatalog(), simulateSeconds: 30, burstBees: 25, stepCounts: [1, 8]);

        // ?burst=25 raises concurrency and updates every burst entry's BeesPerBurst to 25.
        Assert.True(source.Plan.MaxConcurrency >= 75);
        var burstEntries = source.Plan.Scenarios.Where(s => s.Pattern == SpawningPattern.Bursts).ToList();
        Assert.NotEmpty(burstEntries);
        Assert.All(burstEntries, e => Assert.Equal(25, e.BeesPerBurst));

        // ?stepcount=1,8 appends two synthetic stepped entries with 1 and 8 declared steps respectively.
        var stepTestEntries = source.Current.Steps!
            .Where(s => s.Count is 1 or 8)
            .ToList();
        Assert.Contains(stepTestEntries, s => s.Count == 1);
        Assert.Contains(stepTestEntries, s => s.Count == 8);
    }

    [Fact]
    public void Preset_edits_and_seeded_fake_logs_cover_normal_slow_http_500_timeout_and_skipped_calls()
    {
        using var source = new FakeRunSource(BuiltInCatalog(), simulateSeconds: 180);

        // Editing presets: blank name is ignored, overwriting updates in place case-insensitively, out-of-range remove is safe.
        var presetsBefore = source.PresetNames.Count;
        source.SavePreset("   ");
        Assert.Equal(presetsBefore, source.PresetNames.Count);

        source.SavePreset("smoke test"); // overwrites existing "Smoke test" preset case-insensitively
        Assert.Equal(presetsBefore, source.PresetNames.Count);
        source.LoadPreset("DoesNotExist"); // no-op
        source.RemoveEntry(999);           // no-op
        Assert.Equal("smoke test", source.PresetName);

        // Inspect lazy log generation across the seeded run's detailed bees (normal, slow, HTTP 500, timeout, skipped).
        var allLogs = new List<FakeLogEntry>();
        for (var e = 0; e < source.Current.Scenarios.Length; e++)
        {
            foreach (var bee in source.Current.Store.Bees(e))
            {
                var detail = source.Current.Store.Detail(bee.Sequence);
                if (detail is not null)
                    allLogs.AddRange(detail.Logs);
            }
        }

        Assert.NotEmpty(allLogs);
        Assert.Contains(allLogs, l => l.Level == "Information" && l.Message.Contains("→ 200"));
        Assert.Contains(allLogs, l => l.Level == "Warning" && l.Message.Contains("Slow response:"));
        Assert.Contains(allLogs, l => l.Level == "Error" && (l.Message.Contains("Timeout after") || l.Message.Contains("→ 500") || l.Message.Contains("→ 503")));
        Assert.Contains(allLogs, l => l.Level == "Debug" && l.Message.StartsWith("Skipped ", StringComparison.Ordinal));
    }

    [Fact]
    public void Loading_a_preset_refreshes_the_overview_snapshot_to_that_preset()
    {
        using var source = new FakeRunSource(BuiltInCatalog(), simulateSeconds: 30);

        Assert.Equal("Black Friday mix", source.PresetName);
        Assert.Equal(FakeRunState.Finished, source.Current.State);
        var originalCount = source.Current.Count;

        source.LoadPreset("Smoke test");

        Assert.Equal("Smoke test", source.PresetName);
        Assert.Equal(FakeRunState.Idle, source.Current.State);
        Assert.Equal(source.Plan.Scenarios.Count, source.Current.Count);
        Assert.NotEqual(originalCount, source.Current.Count);
        Assert.Equal(
            source.Plan.Scenarios.Select(s => string.IsNullOrWhiteSpace(s.Label) ? s.ScenarioId : s.Label),
            source.Current.Labels);
        Assert.All(source.Current.Scenarios, v => Assert.Equal(0, v.Finished));
    }
}
