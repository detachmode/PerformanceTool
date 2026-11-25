using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios.BuiltIn;

/// <summary>Parameters for <see cref="UserJourneyScenario"/>.</summary>
public sealed class UserJourneyParameters
{
    /// <summary>Gets or sets the ordered list of URLs fetched with HTTP GET during each journey iteration.</summary>
    [Display(Name = "Steps (URLs)", Description = "Fetched with GET, in this order."), HttpUrl, MinLength(1)]
    public List<string> Urls { get; set; } = ["https://example.com", "https://example.com/about"];

    /// <summary>Gets or sets the delay inserted between steps to simulate user reading time; excluded from the measured duration.</summary>
    [Advanced, Display(Name = "Think time", Description = "Pause between steps, like a user reading the page. Not counted in the measured time.")]
    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan ThinkTime { get; set; } = TimeSpan.Zero;

    /// <summary>Gets or sets the number of times the full URL sequence is repeated per bee execution.</summary>
    [Advanced, Display(Name = "Repeat", Description = "How many times the whole journey runs per bee.")]
    [Range(1, 10)]
    public int Iterations { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether the journey aborts immediately when a step fails.</summary>
    [Advanced, Display(Name = "Stop at first failing step")]
    public bool StopOnFailure { get; set; } = true;
}

/// <summary>Several GETs in sequence, like a user clicking through pages. One bar = the whole journey.</summary>
public sealed class UserJourneyScenario : Scenario<UserJourneyParameters>
{
    /// <summary>Gets the stable scenario identifier (<c>user-journey</c>).</summary>
    public override string Id => "user-journey";

    /// <summary>Gets the display name of the scenario.</summary>
    public override string Name => "User journey";

    /// <summary>Gets a short description of what the scenario does.</summary>
    public override string Description => "GETs several URLs one after another; the bar is the total time of the journey.";

    /// <summary>Executes each configured GET step across all iterations, accumulating bytes and request-only elapsed time.</summary>
    /// <param name="p">The scenario parameters controlling URLs, think time, iterations, and failure handling.</param>
    /// <param name="ctx">The execution context used to send each HTTP GET request.</param>
    /// <param name="ct">A token to observe for cancellation.</param>
    /// <returns>The aggregated outcome of the journey, including total bytes and measured duration excluding think time.</returns>
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
