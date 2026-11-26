using Beesharp.Services.Tracing;

namespace Beesharp.Api;

/// <summary>
/// Endpoint mappings for distributed tracing UI configuration.
/// </summary>
public static class TracingEndpoints
{
    /// <summary>
    /// Maps REST endpoints exposing tracing UI configuration to the client.
    /// </summary>
    /// <param name="app">The endpoint route builder to add tracing routes to.</param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> instance for chaining.</returns>
    /// <remarks>
    /// Mapped routes:
    /// <list type="bullet">
    ///   <item>
    ///     <term><c>GET /api/tracing/config</c></term>
    ///     <description>Returns <c>200 OK</c> with <see cref="TracingUiConfig"/> containing the server's configured <c>TraceUrlTemplate</c>.</description>
    ///   </item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapTracingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tracing/config", (TracingSettings settings) =>
            Results.Ok(new TracingUiConfig(settings.TraceUrlTemplate)));
        return app;
    }
}
