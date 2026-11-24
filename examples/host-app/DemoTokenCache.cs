using Beesharp.Services.Scenarios;
using Microsoft.Extensions.Logging;

namespace HostExample;

/// <summary>
/// Plain .NET DI singleton shared across all scenarios: lazily fetches and caches a token
/// with a <see cref="SemaphoreSlim"/> so concurrent bees never stampede the auth endpoint.
/// </summary>
public sealed class DemoTokenCache
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? token;
    private string? cachedEnv;
    private DateTimeOffset expiresAt;
    private int refreshCount;

    public async Task<string> GetTokenAsync(ScenarioContext ctx, CancellationToken ct)
    {
        var env = ctx.Environment.Name;
        if (token is not null && cachedEnv == env && DateTimeOffset.UtcNow < expiresAt)
            return token;

        await gate.WaitAsync(ct);
        try
        {
            if (token is not null && cachedEnv == env && DateTimeOffset.UtcNow < expiresAt)
                return token;

            await ctx.MeasureCallAsync("POST /auth/token", async () =>
            {
                await Task.Delay(25, ct);
                refreshCount++;
                token = $"tok-{refreshCount:000}-{Random.Shared.Next(1000, 9999)}";
                cachedEnv = env;
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(4);
                ctx.Logger.LogInformation("Refreshed shared auth token #{RefreshCount}: {Token}", refreshCount, token);
            });

            return token!;
        }
        finally
        {
            gate.Release();
        }
    }
}
