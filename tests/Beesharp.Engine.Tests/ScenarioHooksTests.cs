using System.Net;
using System.Text;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Engine.Tests;

/// <summary>Startup, background job and teardown hooks of a scenario run in the load runner.</summary>
public sealed class ScenarioHooksTests
{
    [Fact]
    public async Task LoadRunner_RunsStartupBackgroundJobAndTeardownHooks()
    {
        var startupCount = 0;
        var teardownCount = 0;
        var bgTicks = 0;
        var bgCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenTokens = new List<string>();

        var scenario = new DelegateScenario<EmptyScenarioParameters>(
            id: "lifecycle-test",
            name: "Lifecycle Test",
            description: "",
            execute: (_, ctx, _) =>
            {
                lock (seenTokens)
                    seenTokens.Add(ctx.State.TryGetValue("token", out var t) ? t?.ToString() ?? "" : "");
                return Task.FromResult(new ScenarioOutcome(200, 10));
            },
            startup: (_, ctx, _) =>
            {
                Interlocked.Increment(ref startupCount);
                ctx.State["token"] = "tok-initial";
                ctx.RunBackground(async ct =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            var n = Interlocked.Increment(ref bgTicks);
                            ctx.State["token"] = $"tok-{n}";
                            await Task.Delay(30, ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        bgCancelled.TrySetResult(true);
                    }
                });
                return Task.CompletedTask;
            },
            teardown: (_, ctx, _) =>
            {
                Interlocked.Increment(ref teardownCount);
                Assert.True(bgCancelled.Task.IsCompleted);
                return Task.CompletedTask;
            });

        var catalog = new ScenarioCatalog([scenario]);
        var runner = new LoadRunner(catalog);
        var plan = new LoadPlan
        {
            DurationSeconds = 1,
            MaxConcurrency = 5,
            TimeoutSeconds = 5,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "lifecycle-test", RatePerMinute = 180 }],
        };

        var results = new List<ExecutionResult>();
        await runner.RunAsync(
            plan,
            r => { lock (results) results.Add(r); return ValueTask.CompletedTask; },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, startupCount);
        Assert.Equal(1, teardownCount);
        Assert.True(bgTicks >= 1);
        Assert.True(results.Count >= 2);
        Assert.All(seenTokens, t => Assert.StartsWith("tok-", t));
    }
}
