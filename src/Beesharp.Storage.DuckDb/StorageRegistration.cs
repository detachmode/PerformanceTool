using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Services.Storage;

public static class StorageRegistration
{
    /// <summary>
    /// Registers the <see cref="IRunStorage"/> the options name: DuckDB (default) or in-memory. Any other provider, SQLite included,
    /// fails right here with a message naming the supported ones (brain 0054).
    /// </summary>
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
