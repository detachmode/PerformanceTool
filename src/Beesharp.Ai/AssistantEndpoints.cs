using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beesharp.Ai.Contracts;

namespace Beesharp.Ai;

/// <summary>Dependency injection registration and minimal API route mapping for the Beesharp AI assistant (brain 0047).</summary>
public static class AssistantExtensions
{
    /// <summary>
    /// Registers the assistant services in the dependency injection container. Settings are bound from the <c>AI</c> configuration
    /// section (<c>AI__Model</c>, <c>AI__ApiKey</c>, <c>AI__Endpoint</c>, and related keys). The host must also register the port
    /// implementations (<see cref="IPlanWorkspacePort"/>, <see cref="IRunPort"/>, <see cref="IRunHistoryPort"/>,
    /// <see cref="IEnvironmentPort"/>, <see cref="IScriptPort"/>) and Core's <c>ScenarioCatalog</c>.
    /// Missing model configuration does not throw; status queries report that the assistant is not configured.
    /// </summary>
    /// <param name="services">The service collection to register the assistant services into.</param>
    /// <param name="configuration">The application configuration containing the <c>AI</c> section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddBeesharpAi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AssistantOptions.SectionName).Get<AssistantOptions>() ?? new AssistantOptions();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IAssistantChatClientFactory, OpenAiChatClientFactory>();
        services.TryAddSingleton<AssistantSessions>();
        services.TryAddSingleton<AssistantService>();
        return services;
    }

    /// <summary>
    /// Maps the assistant REST endpoints under <c>/api/assistant</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder to add the assistant routes to.</param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> for chaining.</returns>
    /// <remarks>
    /// Mapped routes:
    /// <list type="bullet">
    /// <item><description><c>GET /api/assistant/status</c> — returns <see cref="AssistantStatusDto"/> (200 OK) indicating whether the assistant is configured and which model is active.</description></item>
    /// <item><description><c>POST /api/assistant/chat</c> — accepts <see cref="AssistantChatRequest"/> JSON and returns <see cref="AssistantTurnDto"/> (200 OK); returns 400 Bad Request on empty message, 409 Conflict if the session is already busy, 499 if canceled by the client, or 503 Service Unavailable if the assistant is not configured.</description></item>
    /// <item><description><c>DELETE /api/assistant/sessions/{id}</c> — deletes the in-memory session identified by route parameter <c>id</c>; returns 204 No Content when removed or 404 Not Found if absent.</description></item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapBeesharpAi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assistant");

        // Returns configuration status and active model name.
        group.MapGet("/status", (AssistantService assistant) => assistant.Status());

        // Runs one conversation turn against the assistant tool loop.
        group.MapPost("/chat", async (AssistantChatRequest request, AssistantService assistant, HttpContext http) =>
        {
            try
            {
                return Results.Ok(await assistant.ChatAsync(request, http.RequestAborted));
            }
            catch (AssistantNotConfiguredException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (AssistantBusyException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (OperationCanceledException) { return Results.StatusCode(StatusCodes.Status499ClientClosedRequest); }
        });

        // Evicts an in-memory conversation session by id.
        group.MapDelete("/sessions/{id}", (string id, AssistantService assistant) =>
            assistant.DeleteSession(id) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
