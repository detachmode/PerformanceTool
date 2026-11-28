using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Scripting.Tests;

public class CompiledSourceResolverTests
{
    private sealed class LocalSampleParams
    {
        public string Greeting { get; set; } = "hi";
    }

    private sealed class LocalSampleScenario : Scenario<LocalSampleParams>
    {
        public override string Id => "local-sample";
        public override string Name => "Local Sample";
        public override string Description => "Test compiled scenario.";

        protected override Task<ScenarioOutcome> ExecuteAsync(LocalSampleParams parameters, ScenarioContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ScenarioOutcome(200, parameters.Greeting.Length));
    }

    private sealed class LocalSamplePreflight : PreflightProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken)
        {
            context.Data["ok"] = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Built_in_scenario_resolves_from_embedded_pdb_source()
    {
        var dto = CompiledSourceResolver.ResolveScenario(new HttpGetScenario());

        Assert.Equal("http-get", dto.ScenarioId);
        Assert.Equal("HttpGetScenario.cs", dto.FileName);
        Assert.Equal(CompiledSourceResolver.OriginEmbeddedPdb, dto.Origin);
        Assert.Contains("public sealed class HttpGetScenario", dto.Code);
        Assert.Contains("public sealed class HttpGetParameters", dto.Code);
    }

    [Fact]
    public void Local_compiled_scenario_resolves_source_via_pdb()
    {
        var dto = CompiledSourceResolver.ResolveScenario(new LocalSampleScenario());

        Assert.Equal("local-sample", dto.ScenarioId);
        Assert.Equal("CompiledSourceResolverTests.cs", dto.FileName);
        Assert.True(dto.Origin is CompiledSourceResolver.OriginEmbeddedPdb or CompiledSourceResolver.OriginDisk);
        Assert.Contains("class LocalSampleScenario", dto.Code);
    }

    [Fact]
    public void Compiled_preflight_in_store_exposes_resolved_source_and_is_marked_compiled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "beesharp-preflight-src-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new PreflightStore(dir, compiled: [new LocalSamplePreflight()]);
            var list = store.List();

            var item = Assert.Single(list);
            Assert.True(item.IsCompiled);
            Assert.Equal("CompiledSourceResolverTests.cs", item.FileName);
            Assert.Contains("class LocalSamplePreflight", item.Code);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
