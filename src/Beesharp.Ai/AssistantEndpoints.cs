using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PerformanceTool.Ai.Contracts;

namespace PerformanceTool.Ai;

/// <summary>Registration and routes of the assistant (brain 0047).</summary>
public static class AssistantExtensions
{
    /// <summary>
    /// Registers the assistant. Settings come from the configuration section <c>AI</c> (environment variables <c>AI__Model</c>, <c>AI__ApiKey</c>,
    /// <c>AI__Endpoint</c>, ...). The host must also register the ports: <see cref="IPlanWorkspacePort"/>, <see cref="IRunPort"/>,
    /// <see cref="IRunHistoryPort"/>, <see cref="IEnvironmentPort"/>, <see cref="IScriptPort"/> and Core's <c>ScenarioCatalog</c>.
    /// Without a model nothing fails: the status says the assistant is not configured.
    /// </summary>
    public static IServiceCollection AddBeesharpAi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AssistantOptions.SectionName).Get<AssistantOptions>() ?? new AssistantOptions();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IAssistantChatClientFactory, OpenAiChatClientFactory>();
        services.TryAddSingleton<AssistantSessions>();
        services.TryAddSingleton<AssistantService>();
        return services;
    }

    /// <summary>Maps <c>/api/assistant</c>: <c>GET status</c>, <c>POST chat</c>, <c>DELETE sessions/{id}</c>.</summary>
    public static IEndpointRouteBuilder MapBeesharpAi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assistant");

        group.MapGet("/status", (AssistantService assistant) => assistant.Status());

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

        group.MapDelete("/sessions/{id}", (string id, AssistantService assistant) =>
            assistant.DeleteSession(id) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
