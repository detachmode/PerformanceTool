using System.Collections.Concurrent;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;

namespace Beesharp.Scripting.Tests;

/// <summary>
/// Lifecycle stories for <see cref="PreflightStore"/> and <see cref="ShutdownStore"/>
/// (<see cref="ScriptProgramStore"/>): compiling, loading broken files from disk, logging across levels,
/// invoking external commands via <c>RunProcessAsync</c>, cancellation, failure and deletion rules.
/// </summary>
public sealed class ScriptProgramStoreLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-prog-life-{Guid.NewGuid():N}");
    private readonly string preflightDir;
    private readonly string shutdownDir;
    private readonly string envsDir;

    public ScriptProgramStoreLifecycleTests()
    {
        preflightDir = Path.Combine(root, "scripts", "preflight");
        shutdownDir = Path.Combine(root, "scripts", "shutdown");
        envsDir = Path.Combine(root, "environments");
        Directory.CreateDirectory(preflightDir);
        Directory.CreateDirectory(shutdownDir);
        Directory.CreateDirectory(envsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Preflight_and_shutdown_stores_compile_report_disk_problems_and_delete_script_files()
    {
        await File.WriteAllTextAsync(Path.Combine(preflightDir, "good-check.cs"), """Log("ready");""");
        await File.WriteAllTextAsync(Path.Combine(preflightDir, "broken-check.cs"), "not valid c#;");

        var store = new PreflightStore(preflightDir);
        var problems = await store.LoadAllFromDiskAsync();

        // Broken scripts are reported clearly while valid ones still load.
        var problem = Assert.Single(problems);
        Assert.Equal("broken-check.cs", problem.FileName);
        Assert.Single(store.List());
        Assert.NotNull(store.Get("good-check"));

        // Empty code and compile errors return failure without persisting.
        var empty = await store.CompileAndSaveAsync("empty", null, "   ");
        Assert.False(empty.Success);
        Assert.Contains("cannot be empty", empty.Error);

        var bad = await store.CompileAndSaveAsync("bad", "bad.cs", "int x = ;");
        Assert.False(bad.Success);
        Assert.Equal("Compilation failed.", bad.Error);
        Assert.NotEmpty(bad.Diagnostics);

        // Shutdown store defaults to shutdown.cs and deletes saved files cleanly.
        var shutdownStore = new ShutdownStore(shutdownDir);
        var saved = await shutdownStore.CompileAndSaveAsync(null, null, """Log("shutting down");""");
        Assert.True(saved.Success, saved.Error);
        Assert.Equal("shutdown", saved.Script!.ScriptId);
        Assert.True(File.Exists(Path.Combine(shutdownDir, "shutdown.cs")));

        Assert.True(shutdownStore.Delete("shutdown"));
        Assert.False(shutdownStore.Delete("shutdown"));
        Assert.False(File.Exists(Path.Combine(shutdownDir, "shutdown.cs")));
    }

    [Fact]
    public async Task Preflight_script_logs_all_levels_reads_environment_writes_run_bag_and_runs_external_process()
    {
        var envStore = new EnvironmentStore(envsDir);
        await envStore.SaveAsync(new EnvironmentDefinition
        {
            Name = "Staging",
            Variables = [new EnvironmentVariable { Key = "Tenant", Value = "acme-staging" }],
        });

        var store = new PreflightStore(preflightDir, envStore);
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var shellArgs = OperatingSystem.IsWindows()
            ? "/c echo seeded-ok & echo minor-stderr 1>&2"
            : "-c \\\"echo seeded-ok; echo minor-stderr >&2\\\"";
        var code = $$"""
            using Microsoft.Extensions.Logging;

            Logger.LogTrace("trace detail");
            Logger.LogDebug("debug detail");
            Logger.LogWarning("heads up");
            Logger.LogError(new InvalidOperationException("minor hiccup"), "non-fatal");
            Log($"Preparing tenant {Env["Tenant"]}");
            Data["preparedTenant"] = Env["Tenant"];

            var exit = await RunProcessAsync("{{shell}}", "{{shellArgs}}");
            if (exit != 0) Fail($"Process failed with {exit}");
            """;

        var compiled = await store.CompileAndSaveAsync("seed-env", "seed-env.cs", code);
        Assert.True(compiled.Success, compiled.Error);

        var runData = new RunData(entries: 1);
        // RunExternalProcessAsync streams stdout and stderr concurrently on separate tasks, so onLog must be thread-safe.
        var streamed = new ConcurrentQueue<string>();
        var result = await store.ExecuteAsync("seed-env", "Staging", onLog: streamed.Enqueue, runData: runData);

        Assert.True(result.Success, result.Error);
        Assert.Equal("acme-staging", runData.RunBagFor(new DataWriter(DataWriterKind.Shutdown)).Get<string>("preparedTenant"));
        Assert.Equal(result.Logs.OrderBy(x => x, StringComparer.Ordinal), streamed.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(result.Logs, l => l.Contains("[TRC] trace detail"));
        Assert.Contains(result.Logs, l => l.Contains("[DBG] debug detail"));
        Assert.Contains(result.Logs, l => l.Contains("[WRN] heads up"));
        Assert.Contains(result.Logs, l => l.Contains("[ERR] non-fatal — minor hiccup"));
        Assert.Contains(result.Logs, l => l.Contains("[OUT] seeded-ok"));
        Assert.Contains(result.Logs, l => l.Contains("[ERR] minor-stderr"));
        Assert.Contains(result.Logs, l => l.Contains("Process exited with code 0."));
    }

    [Fact]
    public async Task Program_execution_reports_missing_script_explicit_failure_and_cancellation()
    {
        var store = new PreflightStore(preflightDir);

        // Missing script throws KeyNotFoundException.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ExecuteAsync("nope", null));

        // Fail("") uses default failure message.
        var failBlank = await store.CompileAndSaveAsync("fail-blank", "fail-blank.cs", """Fail("");""");
        Assert.True(failBlank.Success, failBlank.Error);
        var failRes = await store.ExecuteAsync("fail-blank", null);
        Assert.False(failRes.Success);
        Assert.Equal("Preflight failed.", failRes.Error);

        // Caller cancellation stops a waiting preflight cleanly.
        var waitCompile = await store.CompileAndSaveAsync("waiter", "waiter.cs", "await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken);");
        Assert.True(waitCompile.Success, waitCompile.Error);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = await store.ExecuteAsync("waiter", null, ct: cts.Token);
        Assert.False(cancelled.Success);
        Assert.Equal("Preflight was cancelled.", cancelled.Error);
        Assert.Contains(cancelled.Logs, l => l.Contains("[WRN] Preflight was cancelled."));
    }
}
