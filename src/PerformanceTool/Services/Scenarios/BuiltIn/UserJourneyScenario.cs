using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using PerformanceTool.Services.Scenarios.Spec;

namespace PerformanceTool.Services.Scenarios.BuiltIn;

public sealed class UserJourneyParameters
{
    [Display(Name = "Steps (URLs)", Description = "Fetched with GET, in this order."), HttpUrl, MinLength(1)]
    public List<string> Urls { get; set; } = ["https://example.com", "https://example.com/about"];

    [Display(Name = "Think time", Description = "Pause between steps, like a user reading the page. Not counted in the measured time.")]
    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan ThinkTime { get; set; } = TimeSpan.Zero;

    [Display(Name = "Repeat", Description = "How many times the whole journey runs per execution.")]
    [Range(1, 10)]
    public int Iterations { get; set; } = 1;

    [Display(Name = "Stop at first failing step")]
    public bool StopOnFailure { get; set; } = true;
}

/// <summary>Several GETs in sequence, like a user clicking through pages. One bar = the whole journey.</summary>
public sealed class UserJourneyScenario : Scenario<UserJourneyParameters>
{
    public override string Id => "user-journey";
    public override string Name => "User journey";
    public override string Description => "GETs several URLs one after another; the bar is the total request time of the journey.";

    protected override async Task<ScenarioOutcome> ExecuteAsync(UserJourneyParameters p, ScenarioContext ctx, CancellationToken ct)
    {
        long bytes = 0;
        var measured = TimeSpan.Zero;
        ScenarioOutcome? last = null;
        string? firstError = null;
        var step = 0;

        for (var iteration = 0; iteration < p.Iterations; iteration++)
        {
            foreach (var url in p.Urls)
            {
                if (step++ > 0 && p.ThinkTime > TimeSpan.Zero) await Task.Delay(p.ThinkTime, ct);

                var sw = Stopwatch.StartNew();
                last = await ctx.SendAsync(new HttpRequestMessage(HttpMethod.Get, url.Trim()), ct);
                measured += sw.Elapsed;
                bytes += last.Bytes;

                if (!last.Success)
                {
                    firstError ??= $"Step {step} ({url}): {last.FailureReason}";
                    if (p.StopOnFailure) return new ScenarioOutcome(last.StatusCode, bytes, firstError) { Measured = measured };
                }
            }
        }

        return last is null
            ? ScenarioOutcome.Failed("No steps configured.")
            : new ScenarioOutcome(last.StatusCode, bytes, firstError) { Measured = measured };
    }
}
