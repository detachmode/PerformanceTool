using System.Text.Json.Nodes;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Scripting.Tests;

public sealed class ScriptIdeSupportTests
{
    [Fact]
    public void ScriptSdkDropper_DropsSdkFilesGitignoreAndNuGetConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), $"beesharp-sdk-drop-{Guid.NewGuid():N}");
        var sdkSrc = Path.Combine(root, "sdk");
        var binSrc = Path.Combine(root, "bin");
        var scriptsDir = Path.Combine(root, "scripts");
        Directory.CreateDirectory(sdkSrc);
        Directory.CreateDirectory(binSrc);
        try
        {
            File.WriteAllText(Path.Combine(sdkSrc, "Beesharp.Core.1.0.0.nupkg"), "fake-nupkg-v1");
            File.WriteAllText(Path.Combine(binSrc, "Beesharp.Core.dll"), "fake-dll");
            File.WriteAllText(Path.Combine(binSrc, "Beesharp.Core.xml"), "<doc />");

            ScriptSdkDropper.Sync(scriptsDir, sdkSrc, binSrc);

            var dropped = Path.Combine(scriptsDir, ".beesharp");
            Assert.True(File.Exists(Path.Combine(dropped, "Beesharp.Core.1.0.0.nupkg")));
            Assert.True(File.Exists(Path.Combine(dropped, "Beesharp.Core.dll")));
            Assert.True(File.Exists(Path.Combine(dropped, "Beesharp.Core.xml")));
            Assert.Equal("*\n", File.ReadAllText(Path.Combine(dropped, ".gitignore")));

            var nugetConfig = Path.Combine(scriptsDir, "NuGet.config");
            Assert.True(File.Exists(nugetConfig));
            var configXml = File.ReadAllText(nugetConfig);
            Assert.Contains("beesharp-local", configXml);
            Assert.Contains("./.beesharp", configXml);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ScriptSdkDropper_PreservesExistingNuGetConfig_AndCleansStaleNupkgs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"beesharp-sdk-stale-{Guid.NewGuid():N}");
        var sdkSrc = Path.Combine(root, "sdk");
        var binSrc = Path.Combine(root, "bin");
        var scriptsDir = Path.Combine(root, "scripts");
        var dropped = Path.Combine(scriptsDir, ".beesharp");
        Directory.CreateDirectory(sdkSrc);
        Directory.CreateDirectory(binSrc);
        Directory.CreateDirectory(dropped);
        try
        {
            const string customConfig = "<configuration><packageSources><add key=\"custom\" value=\"https://example.com\" /></packageSources></configuration>";
            File.WriteAllText(Path.Combine(scriptsDir, "NuGet.config"), customConfig);
            File.WriteAllText(Path.Combine(dropped, "Beesharp.Core.0.9.0.nupkg"), "old");
            File.WriteAllText(Path.Combine(sdkSrc, "Beesharp.Core.1.0.0.nupkg"), "new");

            ScriptSdkDropper.Sync(scriptsDir, sdkSrc, binSrc);

            Assert.Equal(customConfig, File.ReadAllText(Path.Combine(scriptsDir, "NuGet.config")));
            Assert.False(File.Exists(Path.Combine(dropped, "Beesharp.Core.0.9.0.nupkg")));
            Assert.True(File.Exists(Path.Combine(dropped, "Beesharp.Core.1.0.0.nupkg")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ScriptSdkDropper_SkipsGracefullyWhenSourceMissingOrTargetReadOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"beesharp-sdk-ro-{Guid.NewGuid():N}");
        var missingSdk = Path.Combine(root, "no-sdk");
        var missingBin = Path.Combine(root, "no-bin");
        var scriptsDir = Path.Combine(root, "scripts");
        Directory.CreateDirectory(scriptsDir);
        try
        {
            // Missing source files -> does nothing and does not create .beesharp/
            ScriptSdkDropper.Sync(scriptsDir, missingSdk, missingBin);
            Assert.False(Directory.Exists(Path.Combine(scriptsDir, ".beesharp")));

            // Read-only target directory on Unix -> skips without throwing
            if (!OperatingSystem.IsWindows())
            {
                var sdkSrc = Path.Combine(root, "sdk");
                Directory.CreateDirectory(sdkSrc);
                File.WriteAllText(Path.Combine(sdkSrc, "Beesharp.Core.1.0.0.nupkg"), "pkg");

                File.SetUnixFileMode(scriptsDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    var ex = Record.Exception(() => ScriptSdkDropper.Sync(scriptsDir, sdkSrc, missingBin));
                    Assert.Null(ex);
                }
                finally
                {
                    File.SetUnixFileMode(scriptsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FileBasedAppScript_CompilesRegistersAndTestRuns_WithDirectivesDebugRunAndTypesAtBottom()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beesharp-fba-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var catalog = new ScenarioCatalog([]);
            var store = new ScriptScenarioStore(dir, catalog);

            const string code = """
                #:package Beesharp.Core@1.0.0
                #:property LangVersion=preview

                var executions = 0;

                var scenario = Scenario.DefineSteps<ShopParams, ShopStep>(
                    "File-Based Shop",
                    start: _ => new StepStart(),
                    run: async (step, p, ctx) => step switch
                    {
                        StepStart => await HandleStart(p, ctx),
                        StepFinish f => await HandleFinish(f, ctx),
                    })
                    .WithDescription("File-based app compatible scenario");

                await scenario.DebugRunAsync(args);

                async Task<ShopStep?> HandleStart(ShopParams p, ScenarioContext ctx)
                {
                    executions++;
                    await ctx.MeasureCallAsync($"start-{p.Query}", () => Task.CompletedTask);
                    return new StepFinish(p.Query);
                }

                async Task<ShopStep?> HandleFinish(StepFinish f, ScenarioContext ctx)
                {
                    await ctx.MeasureCallAsync($"finish-{f.Tag}", () => Task.CompletedTask);
                    return null;
                }

                public record ShopParams(string Query = "honey");
                public record StepStart;
                public record StepFinish(string Tag);
                public union ShopStep(StepStart, StepFinish);
                """;

            var compiled = await store.CompileAndRegisterAsync("fba-shop", "fba-shop.cs", code);
            Assert.True(compiled.Success, compiled.Error + " " + string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Line}:{d.Column} {d.Message}")));
            Assert.Equal(["Step start", "Step finish"], compiled.Script!.Steps!.Select(s => s.Name).ToArray());

            var result = await store.TestRunAsync("fba-shop", new JsonObject { ["query"] = "tea" }, null);
            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.Calls.Count);
            Assert.Equal("start-tea", result.Calls[0].Url);
            Assert.Equal("finish-tea", result.Calls[1].Url);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task FileBasedAppScript_PreservesExactLineNumbersInDiagnostics()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beesharp-fba-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ScriptScenarioStore(dir, new ScenarioCatalog([]));
            // Line 1: #:package
            // Line 2: #:property
            // Line 3: blank
            // Line 4: valid statement
            // Line 5: compile error
            const string code =
                "#:package Beesharp.Core@1.0.0\n" +
                "#:property LangVersion=preview\n" +
                "\n" +
                "var ok = 1;\n" +
                "int broken = \"not-an-int\";\n";

            var compiled = await store.CompileAndRegisterAsync("broken-fba", "broken-fba.cs", code);
            Assert.False(compiled.Success);
            var err = Assert.Single(compiled.Diagnostics, d => d.Severity == "Error");
            Assert.Equal(5, err.Line);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DebugRunAsync_RunsStandaloneWithParamOverrides_AndSkipsDuringHostRegistration()
    {
        string? observedName = null;
        int? observedCount = null;
        var startupRan = false;
        var teardownRan = false;

        var builder = Scenario.Define<StandaloneParams>("Standalone Test", async (p, ctx) =>
        {
            observedName = p.Label;
            observedCount = p.Count;
            await ctx.MeasureCallAsync("work", () => Task.CompletedTask);
        })
        .OnStartup((_, _) => { startupRan = true; return Task.CompletedTask; })
        .OnTeardown((_, _) => { teardownRan = true; return Task.CompletedTask; });

        // Inside host scope, DebugRunAsync is a no-op.
        using (Scenario.BeginHostScope(new ScenarioScriptHost()))
        {
            var hostExit = await builder.DebugRunAsync(["--param", "Label=ignored"]);
            Assert.Equal(0, hostExit);
            Assert.False(startupRan);
            Assert.Null(observedName);
        }

        // Outside host scope, DebugRunAsync runs Startup -> Execute -> Teardown with param overrides.
        var exitCode = await builder.DebugRunAsync(["--param", "Label=override", "--param=Count=7"]);
        Assert.Equal(0, exitCode);
        Assert.True(startupRan);
        Assert.True(teardownRan);
        Assert.Equal("override", observedName);
        Assert.Equal(7, observedCount);

        // Failing scenario returns exit code 1.
        var failing = Scenario.Define("Failing Standalone", _ => throw new InvalidOperationException("boom"));
        Assert.Equal(1, await failing.DebugRunAsync());
    }

    public sealed record StandaloneParams(string Label = "default", int Count = 1);
}
