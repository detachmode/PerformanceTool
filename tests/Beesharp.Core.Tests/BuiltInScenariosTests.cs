using System.Net;
using System.Text;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Core.Tests;

/// <summary>
/// End-to-end execution stories for the four built-in HTTP scenarios
/// (<see cref="HttpGetScenario"/>, <see cref="HttpPostJsonScenario"/>,
/// <see cref="CustomRequestScenario"/>, <see cref="UserJourneyScenario"/>).
/// </summary>
public sealed class BuiltInScenariosTests
{
    [Fact]
    public async Task Http_get_checks_health_endpoint_and_enforces_optional_expected_status()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = StubHttp(req =>
        {
            requests.Add(req);
            return req.RequestUri!.AbsolutePath switch
            {
                "/health" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("healthy") },
                "/degraded" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        IScenario scenario = new HttpGetScenario();
        var ctx = new ScenarioContext(http);

        // A healthy 200 response succeeds and trims surrounding whitespace from the URL.
        var ok = await scenario.ExecuteAsync(new HttpGetParameters { Url = "  https://api.shop.test/health  " }, ctx, CancellationToken.None);
        Assert.True(ok.Success);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(Encoding.UTF8.GetByteCount("healthy"), ok.Bytes);
        Assert.Null(ok.FailureReason);

        // Requiring an exact status code catches a 200 when 204 was expected.
        var wrongStatus = await scenario.ExecuteAsync(
            new HttpGetParameters { Url = "https://api.shop.test/health", ExpectedStatus = 204 },
            ctx, CancellationToken.None);
        Assert.False(wrongStatus.Success);
        Assert.Equal("Expected HTTP 204, got 200", wrongStatus.FailureReason);

        // A 503 fails even without an ExpectedStatus override.
        var degraded = await scenario.ExecuteAsync(new HttpGetParameters { Url = "https://api.shop.test/degraded" }, ctx, CancellationToken.None);
        Assert.False(degraded.Success);
        Assert.Equal("HTTP 503", degraded.FailureReason);
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task Http_post_json_sends_utf8_json_payload_and_checks_created_status()
    {
        string? seenBody = null;
        string? seenMediaType = null;
        HttpMethod? seenMethod = null;

        using var http = StubHttp(async req =>
        {
            seenMethod = req.Method;
            seenMediaType = req.Content?.Headers.ContentType?.MediaType;
            seenBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"orderId":"ord-42"}""", Encoding.UTF8, "application/json"),
            };
        });

        IScenario scenario = new HttpPostJsonScenario();
        var parameters = new HttpPostJsonParameters
        {
            Url = "https://api.shop.test/orders",
            Body = """{"sku":"BEE-1","qty":2}""",
            ExpectedStatus = 201,
        };

        var outcome = await scenario.ExecuteAsync(parameters, new ScenarioContext(http), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(201, outcome.StatusCode);
        Assert.Equal(HttpMethod.Post, seenMethod);
        Assert.Equal("application/json", seenMediaType);
        Assert.Equal("""{"sku":"BEE-1","qty":2}""", seenBody);
        Assert.Equal(Encoding.UTF8.GetByteCount("""{"orderId":"ord-42"}"""), outcome.Bytes);
    }

    [Fact]
    public async Task Custom_request_applies_bearer_and_basic_auth_headers_and_body_content_type()
    {
        var seen = new List<(HttpMethod Method, string? Auth, string? CustomHeader, string? ContentType, string? Body)>();

        using var http = StubHttp(async req =>
        {
            var body = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            req.Headers.TryGetValues("X-Tenant", out var tenant);
            seen.Add((
                req.Method,
                req.Headers.Authorization?.ToString(),
                tenant?.FirstOrDefault(),
                req.Content?.Headers.ContentType?.MediaType,
                body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        IScenario scenario = new CustomRequestScenario();
        var ctx = new ScenarioContext(http);

        // Story 1: Authenticated PUT with Bearer token, custom header, content header, and JSON body.
        var bearerParams = new CustomRequestParameters
        {
            Method = HttpVerb.PUT,
            Url = "https://api.shop.test/cart/42",
            Headers = new Dictionary<string, string>
            {
                ["X-Tenant"] = "acme",
                ["Content-Language"] = "en-US",
            },
            ContentType = "application/merge-patch+json",
            Body = """{"qty":5}""",
            Auth = new AuthParameters { Scheme = AuthScheme.Bearer, Token = "tok-live-99" },
            ExpectedStatus = 200,
        };
        var putOutcome = await scenario.ExecuteAsync(bearerParams, ctx, CancellationToken.None);
        Assert.True(putOutcome.Success);

        // Story 2: Basic auth with blank Content-Type falls back to text/plain.
        var basicParams = new CustomRequestParameters
        {
            Method = HttpVerb.POST,
            Url = "https://api.shop.test/webhooks",
            ContentType = "   ",
            Body = "plain event payload",
            Auth = new AuthParameters { Scheme = AuthScheme.Basic, Username = "ops", Password = "s3cret" },
        };
        var postOutcome = await scenario.ExecuteAsync(basicParams, ctx, CancellationToken.None);
        Assert.True(postOutcome.Success);

        // Story 3: Unauthenticated DELETE with empty body sends no content.
        var deleteParams = new CustomRequestParameters
        {
            Method = HttpVerb.DELETE,
            Url = "https://api.shop.test/cart/42",
            Headers = new Dictionary<string, string> { ["Accept"] = "*/*" },
            Body = "",
            Auth = new AuthParameters { Scheme = AuthScheme.None },
        };
        var deleteOutcome = await scenario.ExecuteAsync(deleteParams, ctx, CancellationToken.None);
        Assert.True(deleteOutcome.Success);

        Assert.Equal(3, seen.Count);

        Assert.Equal(HttpMethod.Put, seen[0].Method);
        Assert.Equal("Bearer tok-live-99", seen[0].Auth);
        Assert.Equal("acme", seen[0].CustomHeader);
        Assert.Equal("application/merge-patch+json", seen[0].ContentType);
        Assert.Equal("""{"qty":5}""", seen[0].Body);

        Assert.Equal(HttpMethod.Post, seen[1].Method);
        var expectedBasic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ops:s3cret"));
        Assert.Equal(expectedBasic, seen[1].Auth);
        Assert.Equal("text/plain", seen[1].ContentType);
        Assert.Equal("plain event payload", seen[1].Body);

        Assert.Equal(HttpMethod.Delete, seen[2].Method);
        Assert.Null(seen[2].Auth);
        Assert.Null(seen[2].Body);
    }

    [Fact]
    public async Task User_journey_walks_every_page_repeats_iterations_and_handles_failures()
    {
        var visited = new List<string>();
        using var http = StubHttp(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            visited.Add(path);
            if (path == "/broken")
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("err") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"page:{path}") };
        });

        IScenario scenario = new UserJourneyScenario();
        var ctx = new ScenarioContext(http);

        // Happy path: two pages visited across two iterations with a short think time between steps.
        var happy = new UserJourneyParameters
        {
            Urls = ["https://shop.test/catalog", "https://shop.test/item/7"],
            Iterations = 2,
            ThinkTime = TimeSpan.FromMilliseconds(5),
        };
        var okOutcome = await scenario.ExecuteAsync(happy, ctx, CancellationToken.None);
        Assert.True(okOutcome.Success);
        Assert.Equal(200, okOutcome.StatusCode);
        Assert.NotNull(okOutcome.Measured);
        Assert.Equal(["/catalog", "/item/7", "/catalog", "/item/7"], visited);
        Assert.True(okOutcome.Bytes > 0);

        // StopOnFailure = true stops immediately after the failing step and skips the rest.
        visited.Clear();
        var stopEarly = new UserJourneyParameters
        {
            Urls = ["https://shop.test/catalog", "https://shop.test/broken", "https://shop.test/checkout"],
            StopOnFailure = true,
        };
        var stoppedOutcome = await scenario.ExecuteAsync(stopEarly, ctx, CancellationToken.None);
        Assert.False(stoppedOutcome.Success);
        Assert.Equal(500, stoppedOutcome.StatusCode);
        Assert.Contains("Step 2 (https://shop.test/broken): HTTP 500", stoppedOutcome.FailureReason);
        Assert.Equal(["/catalog", "/broken"], visited);

        // StopOnFailure = false finishes the whole journey while preserving the first error encountered.
        visited.Clear();
        var continuePastError = new UserJourneyParameters
        {
            Urls = ["https://shop.test/catalog", "https://shop.test/broken", "https://shop.test/checkout"],
            StopOnFailure = false,
        };
        var continuedOutcome = await scenario.ExecuteAsync(continuePastError, ctx, CancellationToken.None);
        Assert.False(continuedOutcome.Success);
        Assert.Equal(200, continuedOutcome.StatusCode); // last step returned 200
        Assert.Contains("Step 2 (https://shop.test/broken): HTTP 500", continuedOutcome.FailureReason);
        Assert.Equal(["/catalog", "/broken", "/checkout"], visited);

        // Empty step list fails explicitly rather than crashing or reporting success.
        var emptyOutcome = await scenario.ExecuteAsync(new UserJourneyParameters { Urls = [] }, ctx, CancellationToken.None);
        Assert.False(emptyOutcome.Success);
        Assert.Equal("No steps configured.", emptyOutcome.FailureReason);
    }

    private static HttpClient StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new DelegateHandler((req, _) => Task.FromResult(respond(req))));

    private static HttpClient StubHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new DelegateHandler((req, _) => respond(req)));

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
