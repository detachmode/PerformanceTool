using Microsoft.Extensions.DependencyInjection;
using Beesharp.Services.Storage;

namespace Beesharp.Storage.Tests;

/// <summary>Choosing the run storage (brain 0054: SQLite was dropped; DuckDb and Memory remain).</summary>
public sealed class StorageProviderTests
{
    [Theory]
    [InlineData("Sqlite")]
    [InlineData("sqlite")]
    [InlineData("Postgres")]
    public void An_unsupported_provider_fails_with_a_message_naming_the_supported_ones(string provider)
    {
        var ex = Assert.Throws<NotSupportedException>(() => new ServiceCollection().AddBeesharpStorage(new StorageOptions { Provider = provider }));
        Assert.Contains(provider, ex.Message);
        Assert.Contains("DuckDb", ex.Message);
        Assert.Contains("Memory", ex.Message);
    }

    [Fact]
    public async Task Memory_and_DuckDb_are_registered_by_name_and_DuckDb_is_the_default()
    {
        var root = Path.Combine(Path.GetTempPath(), "beesharp-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var memory = new ServiceCollection().AddBeesharpStorage(new StorageOptions { Provider = "memory" }).BuildServiceProvider())
                Assert.IsType<InMemoryRunStorage>(memory.GetRequiredService<IRunStorage>());

            var options = new StorageOptions { Root = root };
            Assert.Equal(Path.Combine(root, "data", "beesharp.duckdb"), options.ResolveDuckDbPath());
            await using var duck = new ServiceCollection().AddBeesharpStorage(options).BuildServiceProvider();
            var storage = Assert.IsType<DuckDbRunStorage>(duck.GetRequiredService<IRunStorage>());
            Assert.Equal(options.ResolveDuckDbPath(), storage.DatabasePath);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
