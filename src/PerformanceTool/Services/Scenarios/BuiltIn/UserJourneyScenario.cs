namespace PerformanceTool.Services.Scenarios.BuiltIn;

/// <summary>Several GETs in sequence, like a user clicking through pages. One bar = the whole journey.</summary>
public sealed class UserJourneyScenario : IScenario
{
    public string Id => "user-journey";
    public string Name => "User journey";
    public string Description => "GETs several URLs one after another; the bar is the total journey time. Stops at the first failing step.";

    public IReadOnlyList<ScenarioParameter> Parameters { get; } =
    [
        new("urls", "URLs (one per line)", ParameterType.Multiline, "https://example.com\nhttps://example.com/about"),
    ];

    public async Task<ScenarioOutcome> ExecuteAsync(ScenarioContext ctx, CancellationToken ct)
    {
        long bytes = 0;
        ScenarioOutcome last = ScenarioOutcome.Failed("No URLs configured.");
        var step = 0;
        foreach (var url in ScenarioContext.Lines(ctx.Get("urls")))
        {
            step++;
            if (!ScenarioContext.IsHttpUrl(url))
                return ScenarioOutcome.Failed($"Step {step}: invalid URL '{url}'.");
            last = await ctx.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
            bytes += last.Bytes;
            if (!last.Success)
                return last with { Bytes = bytes, Error = last.Error ?? $"Step {step} returned {last.StatusCode}." };
        }
        return last with { Bytes = bytes };
    }
}
