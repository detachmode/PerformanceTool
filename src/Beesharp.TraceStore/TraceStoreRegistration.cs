using Beesharp.Services.Environments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beesharp.Services.TraceStore;

/// <summary>
/// Starts the background trace fetch of a run when its environment names a trace store (brain 0065); with none configured it fetches nothing at all.
/// </summary>
/// <param name="factory">Creates the trace store for the environment's settings.</param>
/// <param name="options">Caps, delays and rate limit (an environment can override the cap and the settle time).</param>
public sealed class TraceFetching(ITraceStoreFactory factory, TraceFetchOptions options)
{
    /// <summary>
    /// Reads <c>TraceStore.*</c> from the run's environment and, when a trace store is configured, creates the session and starts it. Says in the run's log
    /// (Information) whether a trace store is configured and where; a bad configuration is a warning.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="startedUtc">When the run started.</param>
    /// <param name="environment">The run's environment (<see langword="null"/> or empty: nothing is configured).</param>
    /// <param name="storage">Where the fetched traces are kept.</param>
    /// <param name="log">Logger with the <see cref="TraceFetchSession.LogCategory"/> category.</param>
    /// <param name="getEnvironmentVariable">Reads an operating-system variable for <c>${env:NAME}</c> in the token (default: the process environment).</param>
    /// <returns>The started session, or <see langword="null"/> when nothing is to be fetched.</returns>
    public TraceFetchSession? Begin(string runId, DateTimeOffset startedUtc, EnvironmentValues? environment, IRunStorage storage, ILogger log, Func<string, string?>? getEnvironmentVariable = null)
    {
        var resolution = TraceStoreSettings.FromEnvironment(environment ?? EnvironmentValues.None, getEnvironmentVariable);
        if (resolution.Problem is { } problem)
        {
            log.LogWarning("Traces are not fetched: {Problem}", problem);
            return null;
        }
        if (resolution.Settings is not { } settings)
        {
            log.LogInformation("Traces are not fetched: no trace store is configured (set {Kind} and {Url} in the environment to fetch the services' traces into the run)",
                TraceStoreSettings.KindKey, TraceStoreSettings.UrlKey);
            return null;
        }

        ITraceStore store;
        try { store = factory.Create(settings); }
        catch (Exception ex)
        {
            log.LogWarning("Traces are not fetched: {Problem}", ex.Message);
            return null;
        }

        var session = new TraceFetchSession(runId, startedUtc, store, storage, options.With(settings), log);
        session.Start();
        return session;
    }
}

/// <summary>Registers the trace fetching (brain 0065).</summary>
public static class TraceStoreRegistration
{
    /// <summary>
    /// Registers <see cref="TraceFetching"/> with Jaeger support and the default <see cref="TraceFetchOptions"/>. Registrations made earlier (a test's fake store
    /// factory or its own options) are kept. <c>AddBeesharpEngine</c> calls this.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBeesharpTraceStore(this IServiceCollection services)
    {
        services.TryAddSingleton(new TraceFetchOptions());
        services.TryAddSingleton<ITraceStoreFactory>(new TraceStoreFactory());
        services.TryAddSingleton<TraceFetching>();
        return services;
    }
}
