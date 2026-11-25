using System.ComponentModel.DataAnnotations;
using System.Text;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios.BuiltIn;

/// <summary>Parameters for <see cref="HttpPostJsonScenario"/>.</summary>
public sealed class HttpPostJsonParameters
{
    /// <summary>Gets or sets the target URL to receive the HTTP POST request.</summary>
    [Display(Name = "URL"), Required, HttpUrl]
    public string Url { get; set; } = "https://example.com/api";

    /// <summary>Gets or sets the UTF-8 JSON payload sent with an <c>application/json</c> content type.</summary>
    [Display(Name = "JSON body"), Multiline]
    public string Body { get; set; } = """{ "hello": "world" }""";

    /// <summary>Gets or sets the exact HTTP status code required for success, or <see langword="null"/> to accept any 2xx or 3xx status.</summary>
    [Display(Name = "Expected status", Description = "Fail unless the response has exactly this status. Empty = any 2xx/3xx.", Prompt = "any 2xx/3xx")]
    [Range(100, 599), Advanced]
    public int? ExpectedStatus { get; set; }
}

/// <summary>Built-in scenario that posts a JSON request body to a target URL.</summary>
public sealed class HttpPostJsonScenario : Scenario<HttpPostJsonParameters>
{
    /// <summary>Gets the stable scenario identifier (<c>http-post-json</c>).</summary>
    public override string Id => "http-post-json";

    /// <summary>Gets the display name of the scenario.</summary>
    public override string Name => "HTTP POST (JSON)";

    /// <summary>Gets a short description of what the scenario does.</summary>
    public override string Description => "Posts a JSON body to a URL.";

    /// <summary>Executes the HTTP POST request with the configured JSON body and validates the response status.</summary>
    /// <param name="p">The scenario parameters containing the target URL, JSON body, and optional expected status.</param>
    /// <param name="ctx">The execution context used to send the HTTP request.</param>
    /// <param name="ct">A token to observe for cancellation.</param>
    /// <returns>The outcome of the HTTP POST request.</returns>
    protected override async Task<ScenarioOutcome> ExecuteAsync(HttpPostJsonParameters p, ScenarioContext ctx, CancellationToken ct) =>
        (await ctx.SendAsync(new HttpRequestMessage(HttpMethod.Post, p.Url.Trim())
        {
            Content = new StringContent(p.Body, Encoding.UTF8, "application/json"),
        }, ct)).Expect(p.ExpectedStatus);
}
