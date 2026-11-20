namespace PerformanceTool.Services.Scenarios.BuiltIn;

public sealed class HttpGetScenario : IScenario
{
    public string Id => "http-get";
    public string Name => "HTTP GET";
    public string Description => "Fetches a single URL and downloads the full response body.";

    public IReadOnlyList<ScenarioParameter> Parameters { get; } =
    [
        new("url", "URL", ParameterType.Url, "https://example.com"),
    ];

    public Task<ScenarioOutcome> ExecuteAsync(ScenarioContext ctx, CancellationToken ct) =>
        ctx.SendAsync(new HttpRequestMessage(HttpMethod.Get, ctx.Get("url").Trim()), ct);
}
