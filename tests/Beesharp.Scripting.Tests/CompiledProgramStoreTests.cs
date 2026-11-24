using Beesharp.Services;
using Beesharp.Services.Runtime;

namespace Beesharp.Scripting.Tests;

public class CompiledProgramStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "beesharp-prog-store-" + Guid.NewGuid().ToString("N"));

    public CompiledProgramStoreTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    private sealed class SeedDatabasePreflight : PreflightProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken)
        {
            context.Log("seeded");
            context.Data["Key"] = "val";
            return Task.CompletedTask;
        }
    }

    private sealed class CleanupOrdersShutdownProgram : ShutdownProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken)
        {
            context.Log("cleaned");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Store_combines_compiled_programs_and_scripts_and_protects_compiled_from_delete()
    {
        var store = new PreflightStore(dir, environments: null, compiled: [new SeedDatabasePreflight()]);

        var scriptCompile = await store.CompileAndSaveAsync("disk-pre", "disk-pre.cs", "Log(\"from-script\");", persist: true);
        Assert.True(scriptCompile.Success);

        var list = store.List();
        Assert.Equal(2, list.Count);
        var compiled = Assert.Single(list, p => p.ScriptId == "seed-database");
        Assert.Equal("Seed database", compiled.Name);
        Assert.Equal("SeedDatabasePreflight.cs", compiled.FileName);
        Assert.Contains("Compiled C# class", compiled.Code);

        // Execute compiled program
        var runData = new RunData(0);
        var result = await store.ExecuteAsync("seed-database", environmentName: null, runData: runData);
        Assert.True(result.Success);
        Assert.Contains(result.Logs, l => l.Contains("seeded"));
        Assert.Equal("val", runData.RunBagFor(new DataWriter(DataWriterKind.Preflight)).Get<string>("Key"));

        // Compiled entries cannot be deleted; script entries can
        Assert.False(store.Delete("seed-database"));
        Assert.NotNull(store.Get("seed-database"));
        Assert.True(store.Delete("disk-pre"));
        Assert.Null(store.Get("disk-pre"));
    }

    [Fact]
    public void ShutdownProgram_strips_ShutdownProgram_suffix_for_default_id_and_name()
    {
        var program = new CleanupOrdersShutdownProgram();
        Assert.Equal("cleanup-orders", program.Id);
        Assert.Equal("Cleanup orders", program.Name);
    }
}
