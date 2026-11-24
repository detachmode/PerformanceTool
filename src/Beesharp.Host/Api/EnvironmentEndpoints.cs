using Beesharp.Services;
using Beesharp.Services.Environments;

namespace Beesharp.Api;

/// <summary>REST for environments (brain 0033): one JSON file each. Minimal: list, get, save (create or replace), delete.</summary>
public static class EnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/environments", (EnvironmentStore store, CancellationToken ct) => store.ListAsync(ct));
        app.MapGet("/api/environments/{name}", async (string name, EnvironmentStore store, CancellationToken ct) =>
            await store.GetAsync(name, ct) is { } e ? Results.Ok(e) : Results.NotFound());
        app.MapPut("/api/environments/{name}", async (string name, EnvironmentDefinition environment, EnvironmentStore store, CancellationToken ct) =>
        {
            environment.Name = name;
            try { await store.SaveAsync(environment, ct); return Results.NoContent(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapDelete("/api/environments/{name}", async (string name, EnvironmentStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(name, ct);
            return Results.NoContent();
        });
        return app;
    }
}
