using Beesharp.Services;
using Beesharp.Services.Environments;

namespace Beesharp.Api;

/// <summary>
/// Maps REST endpoints for managing named environments (<see cref="EnvironmentDefinition"/>),
/// persisted as one JSON file per environment via <see cref="EnvironmentStore"/> (brain 0033).
/// </summary>
public static class EnvironmentEndpoints
{
    /// <summary>
    /// Maps environment CRUD endpoints under <c>/api/environments</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder to register the environment routes on.</param>
    /// <returns>The <paramref name="app"/> builder for chaining.</returns>
    /// <remarks>
    /// Mapped HTTP routes:
    /// <list type="bullet">
    /// <item>
    /// <term><c>GET /api/environments</c></term>
    /// <description>Lists all readable environments ordered by name. Response: <c>200 OK</c> with a JSON array of <see cref="EnvironmentDefinition"/>.</description>
    /// </item>
    /// <item>
    /// <term><c>GET /api/environments/{name}</c></term>
    /// <description>Gets a single environment by route parameter <c>name</c>. Response: <c>200 OK</c> with <see cref="EnvironmentDefinition"/>, or <c>404 Not Found</c> if not found or unreadable.</description>
    /// </item>
    /// <item>
    /// <term><c>PUT /api/environments/{name}</c></term>
    /// <description>Creates or replaces the environment named by route parameter <c>name</c>. Request body: <see cref="EnvironmentDefinition"/> JSON (<see cref="EnvironmentDefinition.Name"/> is set from <c>name</c>). Response: <c>204 No Content</c> on success, or <c>400 Bad Request</c> with <c>{ "error": string }</c> when validation fails.</description>
    /// </item>
    /// <item>
    /// <term><c>DELETE /api/environments/{name}</c></term>
    /// <description>Deletes the environment named by route parameter <c>name</c> (idempotent). Response: <c>204 No Content</c>.</description>
    /// </item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        // List all stored environments.
        app.MapGet("/api/environments", (EnvironmentStore store, CancellationToken ct) => store.ListAsync(ct));
        // Get a single environment by name.
        app.MapGet("/api/environments/{name}", async (string name, EnvironmentStore store, CancellationToken ct) =>
            await store.GetAsync(name, ct) is { } e ? Results.Ok(e) : Results.NotFound());
        // Create or replace an environment by name.
        app.MapPut("/api/environments/{name}", async (string name, EnvironmentDefinition environment, EnvironmentStore store, CancellationToken ct) =>
        {
            environment.Name = name;
            try { await store.SaveAsync(environment, ct); return Results.NoContent(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        // Delete an environment by name.
        app.MapDelete("/api/environments/{name}", async (string name, EnvironmentStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(name, ct);
            return Results.NoContent();
        });
        return app;
    }
}
