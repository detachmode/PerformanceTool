using System.ComponentModel.DataAnnotations;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios.BuiltIn;

/// <summary>Parameters for <see cref="HttpGetScenario"/>.</summary>
public sealed class HttpGetParameters
{
    /// <summary>Gets or sets the target URL to fetch with an HTTP GET request.</summary>
    [Display(Name = "URL"), Required, HttpUrl]
    public string Url { get; set; } = "https://example.com";

    /// <summary>Gets or sets the exact HTTP status code required for success, or <see langword="null"/> to accept any 2xx or 3xx status.</summary>
    [Display(Name = "Expected status", Description = "Fail unless the response has exactly this status. Empty = any 2xx/3xx.", Prompt = "any 2xx/3xx")]
    [Range(100, 599), Advanced]
    public int? ExpectedStatus { get; set; }
}

/// <summary>Built-in scenario that sends a single HTTP GET request and downloads the response body.</summary>
public sealed class HttpGetScenario : Scenario<HttpGetParameters>
{
    /// <summary>Gets the stable scenario identifier (<c>http-get</c>).</summary>
    public override string Id => "http-get";

    /// <summary>Gets the display name of the scenario.</summary>
    public override string Name => "HTTP GET";

    /// <summary>Gets a short description of what the scenario does.</summary>
    public override string Description => "Fetches a single URL and downloads the full response body.";

    /// <summary>Executes the HTTP GET request and validates the response status against <see cref="HttpGetParameters.ExpectedStatus"/>.</summary>
    /// <param name="p">The scenario parameters containing the target URL and optional expected status.</param>
    /// <param name="ctx">The execution context used to send the HTTP request.</param>
    /// <param name="ct">A token to observe for cancellation.</param>
    /// <returns>The outcome of the HTTP GET request.</returns>
    protected override async Task<ScenarioOutcome> ExecuteAsync(HttpGetParameters p, ScenarioContext ctx, CancellationToken ct) =>
        (await ctx.SendAsync(new HttpRequestMessage(HttpMethod.Get, p.Url.Trim()), ct)).Expect(p.ExpectedStatus);
}
