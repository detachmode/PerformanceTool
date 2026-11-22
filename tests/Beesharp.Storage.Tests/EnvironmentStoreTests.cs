using Microsoft.Extensions.Logging.Abstractions;
using Beesharp.Services;
using Beesharp.Services.Environments;

namespace Beesharp.Storage.Tests;

public sealed class EnvironmentStoreTests
{
    [Fact]
    public async Task The_store_keeps_one_json_file_per_environment()
    {
        var root = Path.Combine(Path.GetTempPath(), "pt-env-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EnvironmentStore(Path.Combine(root, "envs"), NullLogger<EnvironmentStore>.Instance);
            Assert.Empty(await store.ListAsync());

            await store.SaveAsync(new EnvironmentDefinition { Name = "Staging", Variables = [new("BaseUrl", "https://s"), new("", ""), new("Token", "abc")] });
            await store.SaveAsync(new EnvironmentDefinition { Name = "Local", Variables = [new("BaseUrl", "https://localhost")] });

            Assert.Equal(2, Directory.GetFiles(store.Directory, "*.json").Length);
            var all = await store.ListAsync();
            Assert.Equal(["Local", "Staging"], all.Select(e => e.Name));
            var staging = (await store.GetAsync("Staging"))!;
            Assert.Equal(["BaseUrl", "Token"], staging.Variables.Select(v => v.Key)); // order kept, blank row dropped

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new EnvironmentDefinition { Name = "Bad", Variables = [new("a b", "x")] }));
            await store.DeleteAsync("Local");
            Assert.Null(await store.GetAsync("Local"));
            Assert.Single(await store.ListAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
