using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios.BuiltIn;

// Upper-case member names on purpose: they are the HTTP method names and are stored in presets.
/// <summary>
/// HTTP request methods supported by <see cref="CustomRequestParameters"/>.
/// </summary>
public enum HttpVerb
{
    /// <summary>HTTP GET method.</summary>
    GET,

    /// <summary>HTTP POST method.</summary>
    POST,

    /// <summary>HTTP PUT method.</summary>
    PUT,

    /// <summary>HTTP PATCH method.</summary>
    PATCH,

    /// <summary>HTTP DELETE method.</summary>
    DELETE,

    /// <summary>HTTP HEAD method.</summary>
    HEAD,

    /// <summary>HTTP OPTIONS method.</summary>
    OPTIONS
}

/// <summary>
/// Authentication scheme applied to an outgoing request in <see cref="CustomRequestScenario"/>.
/// </summary>
public enum AuthScheme
{
    /// <summary>No <c>Authorization</c> header is added.</summary>
    [Display(Name = "None")] None,

    /// <summary>Adds a <c>Bearer</c> token <c>Authorization</c> header.</summary>
    [Display(Name = "Bearer token")] Bearer,

    /// <summary>Adds an HTTP Basic <c>Authorization</c> header encoded from username and password.</summary>
    [Display(Name = "Basic (user + password)")] Basic,
}

/// <summary>
/// Authentication settings for <see cref="CustomRequestParameters"/>.
/// </summary>
public sealed class AuthParameters
{
    /// <summary>
    /// Gets or sets the authentication scheme to apply.
    /// </summary>
    [Display(Name = "Scheme")]
    public AuthScheme Scheme { get; set; } = AuthScheme.None;

    /// <summary>
    /// Gets or sets the bearer token used when <see cref="Scheme"/> is <see cref="AuthScheme.Bearer"/>.
    /// </summary>
    [Display(Name = "Token", Description = "Used for Bearer. Not saved in presets; use ${env:NAME} to read it from an environment variable."), Secret]
    public string Token { get; set; } = "";

    /// <summary>
    /// Gets or sets the username used when <see cref="Scheme"/> is <see cref="AuthScheme.Basic"/>.
    /// </summary>
    [Display(Name = "Username", Description = "Used for Basic.")]
    public string Username { get; set; } = "";

    /// <summary>
    /// Gets or sets the password used when <see cref="Scheme"/> is <see cref="AuthScheme.Basic"/>.
    /// </summary>
    [Display(Name = "Password", Description = "Used for Basic. Not saved in presets; use ${env:NAME}."), Secret]
    public string Password { get; set; } = "";
}

/// <summary>
/// Parameters configuring a single request executed by <see cref="CustomRequestScenario"/>.
/// </summary>
public sealed class CustomRequestParameters
{
    /// <summary>
    /// Gets or sets the HTTP method to send.
    /// </summary>
    [Display(Name = "Method")]
    public HttpVerb Method { get; set; } = HttpVerb.GET;

    /// <summary>
    /// Gets or sets the target request URL.
    /// </summary>
    [Display(Name = "URL"), Required, HttpUrl]
    public string Url { get; set; } = "https://example.com";

    /// <summary>
    /// Gets or sets custom HTTP headers added to the request or, for content headers, to its body.
    /// </summary>
    [Advanced, Display(Name = "Headers", Description = "Request headers. Content headers are applied to the body.")]
    public Dictionary<string, string> Headers { get; set; } = new() { ["Accept"] = "*/*" };

    /// <summary>
    /// Gets or sets the media type sent when <see cref="Body"/> is non-empty.
    /// </summary>
    [Advanced, Display(Name = "Content-Type")]
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// Gets or sets the request body payload, sent when non-empty.
    /// </summary>
    [Display(Name = "Body", Description = "Sent when not empty."), Multiline]
    public string Body { get; set; } = "";

    /// <summary>
    /// Gets or sets the authentication settings for the request.
    /// </summary>
    [Advanced, Display(Name = "Authentication")]
    public AuthParameters Auth { get; set; } = new();

    /// <summary>
    /// Gets or sets an optional exact HTTP status code required for the execution to succeed; <see langword="null"/> accepts any 2xx or 3xx status.
    /// </summary>
    [Display(Name = "Expected status", Description = "Fail unless the response has exactly this status. Empty = any 2xx/3xx.", Prompt = "any 2xx/3xx")]
    [Range(100, 599), Advanced]
    public int? ExpectedStatus { get; set; }
}

/// <summary>
/// Built-in scenario that sends a configurable HTTP request with custom method, headers, authentication, and optional body.
/// </summary>
public sealed class CustomRequestScenario : Scenario<CustomRequestParameters>
{
    /// <inheritdoc />
    public override string Id => "http-custom";

    /// <inheritdoc />
    public override string Name => "Custom HTTP request";

    /// <inheritdoc />
    public override string Description => "Any method, custom headers, authentication and an optional body.";

    /// <inheritdoc />
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
