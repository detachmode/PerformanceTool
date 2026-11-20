using System.Text;

namespace PerformanceTool.Services.Scenarios.BuiltIn;

public sealed class CustomRequestScenario : IScenario
{
    public string Id => "http-custom";
    public string Name => "Custom HTTP request";
    public string Description => "Any method, custom headers and an optional body.";

    public IReadOnlyList<ScenarioParameter> Parameters { get; } =
    [
        new("method", "Method", ParameterType.Choice, "GET", Options: ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"]),
        new("url", "URL", ParameterType.Url, "https://example.com"),
        new("headers", "Headers", ParameterType.Multiline, "Accept: */*", Help: "One 'Name: value' per line.", Required: false),
        new("contentType", "Content-Type", ParameterType.Text, "application/json", Required: false),
        new("body", "Body", ParameterType.Multiline, "", Required: false),
    ];

    public Task<ScenarioOutcome> ExecuteAsync(ScenarioContext ctx, CancellationToken ct)
    {
        var request = new HttpRequestMessage(new HttpMethod(ctx.Get("method")), ctx.Get("url").Trim());
        var body = ctx.Get("body");
        if (body.Length > 0)
        {
            var contentType = ctx.Get("contentType").Trim();
            request.Content = new StringContent(body, Encoding.UTF8, contentType.Length > 0 ? contentType : "text/plain");
        }

        foreach (var line in ScenarioContext.Lines(ctx.Get("headers")))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        return ctx.SendAsync(request, ct);
    }
}
