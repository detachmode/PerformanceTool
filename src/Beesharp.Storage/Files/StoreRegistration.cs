using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beesharp.Services;

public static class StoreRegistration
{
    /// <summary>Registers <see cref="PresetStore"/> and <see cref="EnvironmentStore"/> (JSON files) for the folders in <paramref name="directories"/>, and the options themselves.</summary>
    public static IServiceCollection AddBeesharpFileStores(this IServiceCollection services, BeesharpDirectories directories)
    {
        services.AddSingleton(directories);
        services.AddSingleton(sp => new PresetStore(directories.ResolvePresets(), sp.GetService<ILogger<PresetStore>>()));
        services.AddSingleton(sp => new EnvironmentStore(directories.ResolveEnvironments(), sp.GetService<ILogger<EnvironmentStore>>()));
        return services;
    }
}
