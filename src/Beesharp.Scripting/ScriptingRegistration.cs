using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Services;

public static class ScriptingRegistration
{
    /// <summary>
    /// Registers the three script stores: <see cref="ScriptScenarioStore"/> (<c>scripts/</c>), <see cref="PreflightStore"/> (<c>scripts/preflight/</c>)
    /// and <see cref="ShutdownStore"/> (<c>scripts/shutdown/</c>). Needs <see cref="BeesharpDirectories"/>, <see cref="ScenarioCatalog"/> and
    /// <see cref="EnvironmentStore"/> in the container (see <c>AddBeesharpFileStores</c>). Loading the files from disk is up to the caller (<c>LoadAllFromDiskAsync</c>).
    /// </summary>
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
