using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Services.Storage;

/// <summary>
/// Dependency injection registration helpers for configuring Beesharp run storage.
/// </summary>
public static class StorageRegistration
{
    /// <summary>
    /// Registers the <see cref="IRunStorage"/> the options name: DuckDB (default) or in-memory. Any other provider, SQLite included,
    /// fails right here with a message naming the supported ones (brain 0054).
    /// </summary>
    /// <param name="services">The service collection to register the storage provider with.</param>
    /// <param name="options">Storage configuration options specifying the provider and DuckDB database path.</param>
    /// <returns>The updated <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddBeesharpStorage(this IServiceCollection services, StorageOptions options)
    {
        if (options.ResolveProvider() == StorageOptions.Memory)
            services.AddSingleton<IRunStorage, InMemoryRunStorage>();
        else
        {
            var path = options.ResolveDuckDbPath();
            services.AddSingleton<IRunStorage>(_ => new DuckDbRunStorage(path));
        }
        return services;
    }
}
