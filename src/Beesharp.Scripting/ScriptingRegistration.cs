using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Services;

/// <summary>Dependency injection registration extensions for Beesharp's Roslyn script stores.</summary>
public static class ScriptingRegistration
{
    /// <summary>
    /// Registers the three script stores: <see cref="ScriptScenarioStore"/> (<c>scripts/</c>), <see cref="PreflightStore"/> (<c>scripts/preflight/</c>)
    /// and <see cref="ShutdownStore"/> (<c>scripts/shutdown/</c>). Needs <see cref="BeesharpDirectories"/>, <see cref="Scenarios.ScenarioCatalog"/> and
    /// <see cref="EnvironmentStore"/> in the container (see <c>AddBeesharpFileStores</c>). Loading the files from disk is up to the caller (<c>LoadAllFromDiskAsync</c>).
    /// </summary>
    /// <param name="services">The service collection to register the script stores into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddBeesharpScripting(this IServiceCollection services)
    {
        services.AddSingleton(sp => new ScriptScenarioStore(
            sp.GetRequiredService<BeesharpDirectories>().ResolveScripts(),
            sp.GetRequiredService<Scenarios.ScenarioCatalog>(),
            sp.GetRequiredService<EnvironmentStore>()));
        services.AddSingleton(sp => new PreflightStore(
            Path.Combine(sp.GetRequiredService<BeesharpDirectories>().ResolveScripts(), "preflight"),
            sp.GetRequiredService<EnvironmentStore>(),
            sp.GetServices<IPreflightProgram>()));
        services.AddSingleton(sp => new ShutdownStore(
            Path.Combine(sp.GetRequiredService<BeesharpDirectories>().ResolveScripts(), "shutdown"),
            sp.GetRequiredService<EnvironmentStore>(),
            sp.GetServices<IShutdownProgram>()));
        return services;
    }
}
