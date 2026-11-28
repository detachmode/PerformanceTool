using Beesharp.Contracts;
using Beesharp.Services;
using Beesharp.Services.Storage;

namespace Beesharp.Storage.Tests;

public class RunSourceSnapshotStorageTests
{
    [Fact]
    public async Task DuckDb_stores_and_deduplicates_run_source_snapshots_by_sha256()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"beesharp-src-{Guid.NewGuid():N}.duckdb");
        try
        {
            await using var storage = new DuckDbRunStorage(dbPath);
            storage.EnqueueRunStarted("r1", DateTimeOffset.UtcNow, new LoadPlan());
            storage.EnqueueRunStarted("r2", DateTimeOffset.UtcNow, new LoadPlan());
            await storage.FlushAsync();

            var s1 = new RunSourceSnapshotDto("scenario", "shop-journey", "Shop journey", "ShopJourneyScenario.cs", "class ShopJourneyScenario {}", "abc123", "EmbeddedPdb", IsCompiled: true);
            var s2 = new RunSourceSnapshotDto("preflight", "warmup", "Warmup", "WarmupPreflight.cs", "class WarmupPreflight {}", "def456", "EmbeddedPdb", IsCompiled: true);

            await storage.SaveRunSourcesAsync("r1", [s1, s2]);
            await storage.SaveRunSourcesAsync("r2", [s1]);

            var loadedR1 = await storage.GetRunSourcesAsync("r1");
            Assert.Equal(2, loadedR1.Count);
            var loadedShop = Assert.Single(loadedR1, x => x.TargetId == "shop-journey");
            Assert.Equal("ShopJourneyScenario.cs", loadedShop.FileName);
            Assert.Equal("class ShopJourneyScenario {}", loadedShop.Code);
            Assert.Equal("abc123", loadedShop.Sha256);
            Assert.True(loadedShop.IsCompiled);

            var loadedR2 = await storage.GetRunSourcesAsync("r2");
            var loadedR2Shop = Assert.Single(loadedR2);
            Assert.Equal(s1.Code, loadedR2Shop.Code);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + ".wal")) File.Delete(dbPath + ".wal");
        }
    }

    [Fact]
    public async Task InMemory_stores_and_retrieves_run_source_snapshots()
    {
        await using var storage = new InMemoryRunStorage();
        storage.EnqueueRunStarted("r1", DateTimeOffset.UtcNow, new LoadPlan());

        var snap = new RunSourceSnapshotDto("scenario", "http-get", "HTTP GET", "HttpGetScenario.cs", "class HttpGetScenario {}", "aaa111", "EmbeddedPdb", IsCompiled: true);
        await storage.SaveRunSourcesAsync("r1", [snap]);

        var loaded = await storage.GetRunSourcesAsync("r1");
        Assert.Equal([snap], loaded);
    }
}
