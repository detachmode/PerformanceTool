using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beesharp.Services;

/// <summary>Dependency injection registration extensions for Beesharp's file-backed preset and environment stores.</summary>
public static class StoreRegistration
{
    /// <summary>Registers <see cref="PresetStore"/> and <see cref="EnvironmentStore"/> (JSON files) for the folders in <paramref name="directories"/>, and the options themselves.</summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="directories">The configured folder paths for presets, environments, and scripts.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddBeesharpFileStores(this IServiceCollection services, BeesharpDirectories directories)
    {
        services.AddSingleton(directories);
        services.AddSingleton(sp => new PresetStore(directories.ResolvePresets(), sp.GetService<ILogger<PresetStore>>()));
        services.AddSingleton(sp => new EnvironmentStore(directories.ResolveEnvironments(), sp.GetService<ILogger<EnvironmentStore>>()));
        return services;
    }
}
