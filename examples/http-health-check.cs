// Example scenario script without steps: every bee runs this body once per entry, top to bottom.
// Copy it into the scripts folder (or mount this folder there, see README.md) and it shows up in the editor.
public record HealthCheckParams(
    [property: Description("Base URL of the service under test")]
    string BaseUrl = "https://example.com",
    [property: Description("Path that is fetched on every run")]
    string Path = "/",
    [property: Description("Fail when the response takes longer than this")]
    [property: Range(10, 60000)]
    int MaxDurationMs = 2000
);

Scenario.Define<HealthCheckParams>("HTTP Health Check", async (p, ctx) =>
{
    var url = p.BaseUrl.TrimEnd('/') + "/" + p.Path.TrimStart('/');

    await ctx.MeasureCallAsync($"GET {p.Path}", async () =>
    {
        var started = DateTime.UtcNow;
        using var response = await ctx.Http.GetAsync(url, ctx.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
        var ms = (DateTime.UtcNow - started).TotalMilliseconds;

        ctx.Log($"{url} answered {(int)response.StatusCode} with {body.Length} chars in {ms:F0} ms");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{url} returned {(int)response.StatusCode}");
        if (ms > p.MaxDurationMs)
            throw new TimeoutException($"{url} took {ms:F0} ms (limit {p.MaxDurationMs} ms)");
    });
})
.WithDescription("Fetches one URL per run and fails on an error status or a slow answer. A plain scenario without steps.");
