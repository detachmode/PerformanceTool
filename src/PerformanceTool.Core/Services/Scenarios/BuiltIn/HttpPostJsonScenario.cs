using System.ComponentModel.DataAnnotations;
using System.Text;
using PerformanceTool.Services.Scenarios.Spec;

namespace PerformanceTool.Services.Scenarios.BuiltIn;

public sealed class HttpPostJsonParameters
{
    [Display(Name = "URL"), Required, HttpUrl]
    public string Url { get; set; } = "https://example.com/api";

    [Display(Name = "JSON body"), Multiline]
    public string Body { get; set; } = """{ "hello": "world" }""";

    [Display(Name = "Expected status", Description = "Fail unless the response has exactly this status. Empty = any 2xx/3xx.", Prompt = "any 2xx/3xx")]
    [Range(100, 599), Advanced]
    public int? ExpectedStatus { get; set; }
}

public sealed class HttpPostJsonScenario : Scenario<HttpPostJsonParameters>
{
    public override string Id => "http-post-json";
    public override string Name => "HTTP POST (JSON)";
    public override string Description => "Posts a JSON body to a URL.";

    protected override async Task<ScenarioOutcome> ExecuteAsync(HttpPostJsonParameters p, ScenarioContext ctx, CancellationToken ct) =>
        (await ctx.SendAsync(new HttpRequestMessage(HttpMethod.Post, p.Url.Trim())
        {
            Content = new StringContent(p.Body, Encoding.UTF8, "application/json"),
        }, ct)).Expect(p.ExpectedStatus);
}
