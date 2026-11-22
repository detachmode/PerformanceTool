using System.ComponentModel.DataAnnotations;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios.BuiltIn;

public sealed class HttpGetParameters
{
    [Display(Name = "URL"), Required, HttpUrl]
    public string Url { get; set; } = "https://example.com";

    [Display(Name = "Expected status", Description = "Fail unless the response has exactly this status. Empty = any 2xx/3xx.", Prompt = "any 2xx/3xx")]
    [Range(100, 599), Advanced]
    public int? ExpectedStatus { get; set; }
}

public sealed class HttpGetScenario : Scenario<HttpGetParameters>
{
    public override string Id => "http-get";
    public override string Name => "HTTP GET";
    public override string Description => "Fetches a single URL and downloads the full response body.";

    protected override async Task<ScenarioOutcome> ExecuteAsync(HttpGetParameters p, ScenarioContext ctx, CancellationToken ct) =>
        (await ctx.SendAsync(new HttpRequestMessage(HttpMethod.Get, p.Url.Trim()), ct)).Expect(p.ExpectedStatus);
}
