using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios.BuiltIn;

// Upper-case member names on purpose: they are the HTTP method names and are stored in presets.
public enum HttpVerb { GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS }

public enum AuthScheme
{
    [Display(Name = "None")] None,
    [Display(Name = "Bearer token")] Bearer,
    [Display(Name = "Basic (user + password)")] Basic,
}

public sealed class AuthParameters
{
    [Display(Name = "Scheme")]
    public AuthScheme Scheme { get; set; } = AuthScheme.None;

    [Display(Name = "Token", Description = "Used for Bearer. Not saved in presets; use ${env:NAME} to read it from an environment variable."), Secret]
    public string Token { get; set; } = "";

    [Display(Name = "Username", Description = "Used for Basic.")]
    public string Username { get; set; } = "";

    [Display(Name = "Password", Description = "Used for Basic. Not saved in presets; use ${env:NAME}."), Secret]
    public string Password { get; set; } = "";
}

public sealed class CustomRequestParameters
{
    [Display(Name = "Method")]
    public HttpVerb Method { get; set; } = HttpVerb.GET;

    [Display(Name = "URL"), Required, HttpUrl]
    public string Url { get; set; } = "https://example.com";

    [Advanced, Display(Name = "Headers", Description = "Request headers. Content headers are applied to the body.")]
    public Dictionary<string, string> Headers { get; set; } = new() { ["Accept"] = "*/*" };

    [Advanced, Display(Name = "Content-Type")]
    public string ContentType { get; set; } = "application/json";

    [Display(Name = "Body", Description = "Sent when not empty."), Multiline]
    public string Body { get; set; } = "";

    [Advanced, Display(Name = "Authentication")]
    public AuthParameters Auth { get; set; } = new();

    [Display(Name = "Expected status", Description = "Fail unless the response has exactly this status. Empty = any 2xx/3xx.", Prompt = "any 2xx/3xx")]
    [Range(100, 599), Advanced]
    public int? ExpectedStatus { get; set; }
}

public sealed class CustomRequestScenario : Scenario<CustomRequestParameters>
{
    public override string Id => "http-custom";
    public override string Name => "Custom HTTP request";
    public override string Description => "Any method, custom headers, authentication and an optional body.";

    protected override async Task<ScenarioOutcome> ExecuteAsync(CustomRequestParameters p, ScenarioContext ctx, CancellationToken ct)
    {
        var request = new HttpRequestMessage(new HttpMethod(p.Method.ToString()), p.Url.Trim());
        if (p.Body.Length > 0)
        {
            var contentType = p.ContentType.Trim();
            request.Content = new StringContent(p.Body, Encoding.UTF8, contentType.Length > 0 ? contentType : "text/plain");
        }

        foreach (var (name, value) in p.Headers)
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);

        request.Headers.Authorization = p.Auth.Scheme switch
        {
            AuthScheme.Bearer => new AuthenticationHeaderValue("Bearer", p.Auth.Token),
            AuthScheme.Basic => new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{p.Auth.Username}:{p.Auth.Password}"))),
            _ => request.Headers.Authorization,
        };

        return (await ctx.SendAsync(request, ct)).Expect(p.ExpectedStatus);
    }
}
