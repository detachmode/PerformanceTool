using System.Text.Json.Nodes;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Scripting.Tests;

public class UnionStepsScriptTests
{
    [Fact]
    public async Task Compiles_and_test_runs_union_step_script_with_repeats_and_failures()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beesharp-steps-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var catalog = new ScenarioCatalog([]);
            var store = new ScriptScenarioStore(dir, catalog);

            const string code = """
                public record DemoParams(int Repeats = 3, bool FailOnSecond = false);
                public record LogIn;
                public record OpenItem(int Index);
                public record Pay;
                public union DemoStep(LogIn, OpenItem, Pay);

                Scenario.DefineSteps<DemoParams, DemoStep>(
                    "Union Demo",
                    start: _ => new LogIn(),
                    run: async (step, p, ctx) => step switch
                    {
                        LogIn => await DoLogIn(ctx),
                        OpenItem o => await DoOpen(o, p, ctx),
                        Pay => await DoPay(ctx),
                    });

                async Task<DemoStep?> DoLogIn(ScenarioContext ctx)
                {
                    await ctx.MeasureCallAsync("login", () => Task.CompletedTask);
                    return new OpenItem(0);
                }

                async Task<DemoStep?> DoOpen(OpenItem o, DemoParams p, ScenarioContext ctx)
                {
                    await ctx.MeasureCallAsync($"open-{o.Index}", () =>
                    {
                        if (p.FailOnSecond && o.Index == 1) throw new InvalidOperationException("bad item");
                        return Task.CompletedTask;
                    });
                    return o.Index + 1 < p.Repeats ? o with { Index = o.Index + 1 } : new Pay();
                }

                async Task<DemoStep?> DoPay(ScenarioContext ctx)
                {
                    await ctx.MeasureCallAsync("pay", () => Task.CompletedTask);
                    return null;
                }
                """;

            var compiled = await store.CompileAndRegisterAsync("union-demo", "union-demo.cs", code);
            Assert.True(compiled.Success, compiled.Error + " " + string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
            Assert.Equal(["Log in", "Open item", "Pay"], compiled.Script!.Steps!.Select(s => s.Name).ToArray());

            var ok = await store.TestRunAsync("union-demo", new JsonObject { ["repeats"] = 3, ["failOnSecond"] = false }, null);
            Assert.True(ok.Success, ok.Error);
            Assert.Equal(-1, ok.FailedStep);
            Assert.Equal(5, ok.Calls.Count);
            Assert.Equal([(0, 0), (1, 0), (1, 1), (1, 2), (2, 0)], ok.Calls.Select(c => (c.Step, c.Rep)).ToArray());

            var fail = await store.TestRunAsync("union-demo", new JsonObject { ["repeats"] = 3, ["failOnSecond"] = true }, null);
            Assert.False(fail.Success);
            Assert.Equal(1, fail.FailedStep);
            Assert.Equal(1, fail.FailedRep);
            Assert.Equal("bad item", fail.Error);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Example_checkout_steps_script_compiles_and_runs()
    {
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "examples", "checkout-steps.cs"));
        Assert.True(File.Exists(scriptPath), $"Missing example script at {scriptPath}");

        var dir = Path.Combine(Path.GetTempPath(), $"beesharp-example-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var catalog = new ScenarioCatalog([]);
            var store = new ScriptScenarioStore(dir, catalog);
            var code = await File.ReadAllTextAsync(scriptPath);

            var compiled = await store.CompileAndRegisterAsync("checkout-steps", "checkout-steps.cs", code);
            Assert.True(compiled.Success, compiled.Error + " " + string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
            Assert.Equal(["Log in", "Search", "Open product", "Add to cart", "Pay"], compiled.Script!.Steps!.Select(s => s.Name).ToArray());

            var result = await store.TestRunAsync(
                "checkout-steps",
                new JsonObject { ["openCount"] = 2, ["addToCartPercent"] = 100, ["baseDelayMs"] = 10, ["openFailPercent"] = 0, ["payRetryPercent"] = 0, ["payFailPercent"] = 0 },
                null);
            Assert.True(result.Success, result.Error);
            // LogIn(0) -> Search(1) -> Open(2) -> AddToCart(3) -> Open(2) -> AddToCart(3) -> Pay(4)
            Assert.Equal([0, 1, 2, 3, 2, 3, 4], result.Calls.Select(c => c.Step).ToArray());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
