using Beesharp.Services;
using Microsoft.Extensions.Logging;

namespace HostExample;

/// <summary>
/// Example compiled C# preflight program with DI constructor injection.
/// Primes <see cref="DemoTokenCache"/> before bees start and writes run metadata to <see cref="ProgramContext.Data"/>.
/// </summary>
public sealed class WarmupPreflight(DemoTokenCache tokenCache) : PreflightProgram
{
    public override async Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken)
    {
        var token = await tokenCache.WarmupAsync(context.Logger, context.Environment.Name, cancellationToken);
        context.Data["StartedBy"] = nameof(WarmupPreflight);
        context.Data["WarmedToken"] = token;
        context.Logger.LogInformation("Preflight ready (StartedBy={StartedBy})", nameof(WarmupPreflight));
    }
}
