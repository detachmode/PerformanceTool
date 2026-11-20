using System.Text;

namespace PerformanceTool.Services.Scenarios.BuiltIn;

public sealed class HttpPostJsonScenario : IScenario
{
    public string Id => "http-post-json";
    public string Name => "HTTP POST (JSON)";
    public string Description => "Posts a JSON body to a URL.";

    public IReadOnlyList<ScenarioParameter> Parameters { get; } =
    [
        new("url", "URL", ParameterType.Url, "https://example.com/api"),
        new("body", "JSON body", ParameterType.Multiline, """{ "hello": "world" }"""),
    ];

    public Task<ScenarioOutcome> ExecuteAsync(ScenarioContext ctx, CancellationToken ct) =>
        ctx.SendAsync(new HttpRequestMessage(HttpMethod.Post, ctx.Get("url").Trim())
        {
            Content = new StringContent(ctx.Get("body"), Encoding.UTF8, "application/json"),
        }, ct);
}
