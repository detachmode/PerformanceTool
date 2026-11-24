using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios;

/// <summary>Marker parameter type for parameterless custom scripts.</summary>
public sealed class EmptyScenarioParameters;

/// <summary>
/// Static entry point for defining custom scenarios in standalone .NET 11 file-based apps (<c>#:package Beesharp.Core@1.0.0</c>)
/// as well as inside Beesharp's Roslyn script host (brain 0057). When executing inside Beesharp Server/CLI,
/// <see cref="BeginHostScope"/> routes registrations into the active <see cref="ScenarioScriptHost"/> and marks
/// <see cref="IsHostRegistering"/> so <see cref="ScenarioBuilder{TParameters}.DebugRunAsync"/> is a no-op.
/// </summary>
public static class Scenario
{
    private static readonly AsyncLocal<ScenarioScriptHost?> currentHost = new();

    /// <summary><c>true</c> while Beesharp Server or CLI is executing a script to register scenarios.</summary>
    public static bool IsHostRegistering => currentHost.Value is not null;

    /// <summary>Routes static <see cref="Scenario"/> registrations on the current async flow into <paramref name="host"/>.</summary>
    public static IDisposable BeginHostScope(ScenarioScriptHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var previous = currentHost.Value;
        currentHost.Value = host;
        return new HostScope(previous);
    }

    private sealed class HostScope(ScenarioScriptHost? previous) : IDisposable
    {
        public void Dispose() => currentHost.Value = previous;
    }

    private static ScenarioScriptHost ActiveHost => currentHost.Value ?? new ScenarioScriptHost();

    /// <summary>Defines a scenario whose delegate returns <see cref="Task"/> (completing without throwing counts as success).</summary>
    public static ScenarioBuilder<TParameters> Define<TParameters>(
        string name,
        Func<TParameters, ScenarioContext, Task> execute,
        string? id = null,
        string? description = null) where TParameters : class =>
        ActiveHost.Define(name, execute, id, description);

    /// <summary>Defines a scenario whose delegate returns a <see cref="ScenarioOutcome"/> directly.</summary>
    public static ScenarioBuilder<TParameters> Define<TParameters>(
        string name,
        Func<TParameters, ScenarioContext, Task<ScenarioOutcome>> execute,
        string? id = null,
        string? description = null) where TParameters : class =>
        ActiveHost.Define(name, execute, id, description);

    /// <summary>Defines a parameterless scenario.</summary>
    public static ScenarioBuilder<EmptyScenarioParameters> Define(
        string name,
        Func<ScenarioContext, Task> execute,
        string? id = null,
        string? description = null) =>
        ActiveHost.Define(name, execute, id, description);

    /// <summary>
    /// Defines a step-machine scenario whose steps are declared as a C# 15 <c>union</c> (brain 0056).
    /// Return the next step from <paramref name="run"/>, or <c>null</c> to finish the bee cleanly.
    /// </summary>
    public static ScenarioBuilder<TParameters> DefineSteps<TParameters, TStep>(
        string name,
        Func<TParameters, TStep> start,
        Func<TStep, TParameters, ScenarioContext, Task<TStep?>> run,
        string? id = null,
        string? description = null)
        where TParameters : class
        where TStep : struct, IUnion =>
        ActiveHost.DefineSteps(name, start, run, id, description);

    /// <summary>
    /// Defines a step-machine scenario with per-bee shared state (e.g. auth token, cart id) so step records stay small.
    /// </summary>
    public static ScenarioBuilder<TParameters> DefineSteps<TParameters, TState, TStep>(
        string name,
        Func<TParameters, ScenarioContext, TState> init,
        Func<TParameters, TStep> start,
        Func<TStep, TParameters, TState, ScenarioContext, Task<TStep?>> run,
        string? id = null,
        string? description = null)
        where TParameters : class
        where TStep : struct, IUnion =>
        ActiveHost.DefineSteps(name, init, start, run, id, description);

    /// <summary>Defines a parameterless step-machine scenario.</summary>
    public static ScenarioBuilder<EmptyScenarioParameters> DefineSteps<TStep>(
        string name,
        Func<TStep> start,
        Func<TStep, ScenarioContext, Task<TStep?>> run,
        string? id = null,
        string? description = null)
        where TStep : struct, IUnion =>
        ActiveHost.DefineSteps(name, start, run, id, description);

    /// <summary>
    /// Defines a step-machine scenario whose step handler yields zero, one or many follow-up steps via <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    public static ScenarioBuilder<TParameters> DefineSteps<TParameters, TStep>(
        string name,
        Func<TParameters, TStep> start,
        Func<TStep, TParameters, ScenarioContext, IAsyncEnumerable<TStep>> run,
        string? id = null,
        string? description = null)
        where TParameters : class
        where TStep : struct, IUnion =>
        ActiveHost.DefineSteps(name, start, run, id, description);

    /// <summary>Defines a stateful step-machine scenario whose step handler yields follow-up steps via <see cref="IAsyncEnumerable{T}"/>.</summary>
    public static ScenarioBuilder<TParameters> DefineSteps<TParameters, TState, TStep>(
        string name,
        Func<TParameters, ScenarioContext, TState> init,
        Func<TParameters, TStep> start,
        Func<TStep, TParameters, TState, ScenarioContext, IAsyncEnumerable<TStep>> run,
        string? id = null,
        string? description = null)
        where TParameters : class
        where TStep : struct, IUnion =>
        ActiveHost.DefineSteps(name, init, start, run, id, description);

    /// <summary>Defines a parameterless step-machine scenario whose step handler yields follow-up steps via <see cref="IAsyncEnumerable{T}"/>.</summary>
    public static ScenarioBuilder<EmptyScenarioParameters> DefineSteps<TStep>(
        string name,
        Func<TStep> start,
        Func<TStep, ScenarioContext, IAsyncEnumerable<TStep>> run,
        string? id = null,
        string? description = null)
        where TStep : struct, IUnion =>
        ActiveHost.DefineSteps(name, start, run, id, description);
}

/// <summary>
/// Injected as the <c>Scenario</c> global in C# scenario scripts (brain 0040).
/// Example:
/// <code>
/// Scenario.Define&lt;ApiLoadParams&gt;("Order API Stress Test", async (p, ctx) =&gt; { ... });
/// </code>
/// </summary>
public sealed class ScenarioScriptHost
{
    private readonly List<IScenarioDefinitionBuilder> builders = [];

    /// <summary>Defines a scenario whose delegate returns <see cref="Task"/> (completing without throwing counts as success).</summary>
    public ScenarioBuilder<TParameters> Define<TParameters>(
        string name,
        Func<TParameters, ScenarioContext, Task> execute,
        string? id = null,
        string? description = null) where TParameters : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(execute);
        var builder = new ScenarioBuilder<TParameters>(
            id ?? Slugify(name),
            name.Trim(),
            description ?? "",
            async (p, ctx, _) =>
            {
                await execute(p, ctx);
                return new ScenarioOutcome(200, 0);
            });
        builders.Add(builder);
        return builder;
    }

    /// <summary>Defines a scenario whose delegate returns a <see cref="ScenarioOutcome"/> directly.</summary>
    public ScenarioBuilder<TParameters> Define<TParameters>(
        string name,
        Func<TParameters, ScenarioContext, Task<ScenarioOutcome>> execute,
        string? id = null,
        string? description = null) where TParameters : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(execute);
        var builder = new ScenarioBuilder<TParameters>(
            id ?? Slugify(name),
            name.Trim(),
            description ?? "",
            (p, ctx, _) => execute(p, ctx));
        builders.Add(builder);
        return builder;
    }

    /// <summary>Defines a parameterless scenario.</summary>
    public ScenarioBuilder<EmptyScenarioParameters> Define(
        string name,
        Func<ScenarioContext, Task> execute,
        string? id = null,
        string? description = null) =>
        Define<EmptyScenarioParameters>(name, (_, ctx) => execute(ctx), id, description);

    /// <summary>
    /// Defines a step-machine scenario whose steps are declared as a C# 15 <c>union</c> (brain 0056).
    /// Return the next step from <paramref name="run"/>, or <c>null</c> to finish the bee cleanly.
    /// </summary>
    public ScenarioBuilder<TParameters> DefineSteps<TParameters, TStep>(
        string name,
        Func<TParameters, TStep> start,
        Func<TStep, TParameters, ScenarioContext, Task<TStep?>> run,
        string? id = null,
        string? description = null)
        where TParameters : class
        where TStep : struct, IUnion
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(run);
        var union = StepUnionReflection.Inspect<TStep>();
        var builder = new ScenarioBuilder<TParameters>(
            id ?? Slugify(name),
            name.Trim(),
            description ?? "",
            (p, ctx, ct) => StepMachineRunner.RunAsync(union, start(p), (step, c, _) => run(step, p, c), ctx, ct),
            union.Steps);
        builders.Add(builder);
        return builder;
    }

    /// <summary>
    /// Defines a step-machine scenario with per-bee shared state (e.g. auth token, cart id) so step records stay small.
    /// </summary>
    public ScenarioBuilder<TParameters> DefineSteps<TParameters, TState, TStep>(
        string name,
        Func<TParameters, ScenarioContext, TState> init,
        Func<TParameters, TStep> start,
        Func<TStep, TParameters, TState, ScenarioContext, Task<TStep?>> run,
        string? id = null,
        string? description = null)
        where TParameters : class
        where TStep : struct, IUnion
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(init);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(run);
        var union = StepUnionReflection.Inspect<TStep>();
        var builder = new ScenarioBuilder<TParameters>(
            id ?? Slugify(name),
            name.Trim(),
            description ?? "",
            (p, ctx, ct) =>
            {
                var state = init(p, ctx);
                return StepMachineRunner.RunAsync(union, start(p), (step, c, _) => run(step, p, state, c), ctx, ct);
            },
            union.Steps);
        builders.Add(builder);
        return builder;
    }

    /// <summary>Defines a parameterless step-machine scenario.</summary>
    public ScenarioBuilder<EmptyScenarioParameters> DefineSteps<TStep>(
        string name,
        Func<TStep> start,
        Func<TStep, ScenarioContext, Task<TStep?>> run,
        string? id = null,
        string? description = null)
        where TStep : struct, IUnion =>
        DefineSteps<EmptyScenarioParameters, TStep>(name, _ => start(), (step, _, ctx) => run(step, ctx), id, description);

    /// <summary>
    /// Defines a step-machine scenario whose step handler yields zero, one or many follow-up steps via <see cref="IAsyncEnumerable{T}"/>
    /// (e.g. a Search step yielding several OpenProduct steps followed by AddToCart).
    /// </summary>
    public ScenarioBuilder<TParameters> DefineSteps<TParameters, TStep>(
        string name,
        Func<TParameters, TStep> start,
        Func<TStep, TParameters, ScenarioContext, IAsyncEnumerable<TStep>> run,
        string? id = null,
        string? description = null)
        where TParameters : class
        where TStep : struct, IUnion
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(run);
        var union = StepUnionReflection.Inspect<TStep>();
        var builder = new ScenarioBuilder<TParameters>(
            id ?? Slugify(name),
            name.Trim(),
            description ?? "",
            (p, ctx, ct) => StepMachineRunner.RunManyAsync(union, start(p), (step, c, _) => run(step, p, c), ctx, ct),
            union.Steps);
        builders.Add(builder);
        return builder;
    }

    /// <summary>Defines a stateful step-machine scenario whose step handler yields follow-up steps via <see cref="IAsyncEnumerable{T}"/>.</summary>
    public ScenarioBuilder<TParameters> DefineSteps<TParameters, TState, TStep>(
        string name,
        Func<TParameters, ScenarioContext, TState> init,
        Func<TParameters, TStep> start,
        Func<TStep, TParameters, TState, ScenarioContext, IAsyncEnumerable<TStep>> run,
        string? id = null,
        string? description = null)
        where TParameters : class
        where TStep : struct, IUnion
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(init);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(run);
        var union = StepUnionReflection.Inspect<TStep>();
        var builder = new ScenarioBuilder<TParameters>(
            id ?? Slugify(name),
            name.Trim(),
            description ?? "",
            (p, ctx, ct) =>
            {
                var state = init(p, ctx);
                return StepMachineRunner.RunManyAsync(union, start(p), (step, c, _) => run(step, p, state, c), ctx, ct);
            },
            union.Steps);
        builders.Add(builder);
        return builder;
    }

    /// <summary>Defines a parameterless step-machine scenario whose step handler yields follow-up steps via <see cref="IAsyncEnumerable{T}"/>.</summary>
    public ScenarioBuilder<EmptyScenarioParameters> DefineSteps<TStep>(
        string name,
        Func<TStep> start,
        Func<TStep, ScenarioContext, IAsyncEnumerable<TStep>> run,
        string? id = null,
        string? description = null)
        where TStep : struct, IUnion =>
        DefineSteps<EmptyScenarioParameters, TStep>(name, _ => start(), (step, _, ctx) => run(step, ctx), id, description);

    /// <summary>Builds all scenarios registered by the script. Validates their parameter specs immediately.</summary>
    public IReadOnlyList<IScenario> BuildAll() => builders.Select(b => b.Build()).ToList();

    public static string Slugify(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "custom-scenario" : $"script-{slug}";
    }
}

internal interface IScenarioDefinitionBuilder
{
    IScenario Build();
}

/// <summary>Fluent builder returned by <c>Scenario.Define&lt;TParameters&gt;</c> for attaching lifecycle hooks and metadata.</summary>
public sealed class ScenarioBuilder<TParameters> : IScenarioDefinitionBuilder where TParameters : class
{
    private string id;
    private readonly string name;
    private string description;
    private readonly Func<TParameters, ScenarioContext, CancellationToken, Task<ScenarioOutcome>> execute;
    private readonly IReadOnlyList<StepInfo> steps;
    private Func<TParameters, ScenarioContext, CancellationToken, Task>? startup;
    private Func<TParameters, ScenarioContext, CancellationToken, Task>? teardown;

    internal ScenarioBuilder(
        string id,
        string name,
        string description,
        Func<TParameters, ScenarioContext, CancellationToken, Task<ScenarioOutcome>> execute,
        IReadOnlyList<StepInfo>? steps = null)
    {
        this.id = id;
        this.name = name;
        this.description = description;
        this.execute = execute;
        this.steps = steps ?? [];
    }

    public ScenarioBuilder<TParameters> WithId(string customId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customId);
        id = customId.Trim();
        return this;
    }

    public ScenarioBuilder<TParameters> WithDescription(string text)
    {
        description = text ?? "";
        return this;
    }

    /// <summary>Attaches a lifecycle startup hook that runs once per scenario entry before any bees start.</summary>
    public ScenarioBuilder<TParameters> OnStartup(Func<TParameters, ScenarioContext, Task> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        startup = (p, ctx, _) => hook(p, ctx);
        return this;
    }

    /// <summary>Attaches a lifecycle teardown hook that runs once per scenario entry after all bees finish.</summary>
    public ScenarioBuilder<TParameters> OnTeardown(Func<TParameters, ScenarioContext, Task> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        teardown = (p, ctx, _) => hook(p, ctx);
        return this;
    }

    public IScenario Build() => new DelegateScenario<TParameters>(id, name, description, execute, startup, teardown, steps);

    /// <summary>
    /// Runs a single bee locally when executed as a standalone .NET 11 file-based app (<c>dotnet run my-scenario.cs</c> or IDE F5).
    /// When loaded inside Beesharp Server/CLI (<see cref="Scenario.IsHostRegistering"/> is <c>true</c>), returns <c>0</c> immediately
    /// without executing a bee (brain 0057).
    /// Supports <c>--param Key=Value</c> (repeatable) and <c>--timeout &lt;seconds&gt;</c> in <paramref name="args"/>.
    /// </summary>
    public async Task<int> DebugRunAsync(string[]? args = null, CancellationToken ct = default)
    {
        if (Scenario.IsHostRegistering)
            return 0;

        var scenario = Build();
        var json = scenario.Spec.DefaultJson();
        int? timeoutSeconds = null;

        if (args is { Length: > 0 })
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg is "--param" or "-p")
                {
                    if (i + 1 < args.Length)
                        ApplyParamOverride(scenario.Spec, json, args[++i]);
                }
                else if (arg.StartsWith("--param=", StringComparison.Ordinal))
                {
                    ApplyParamOverride(scenario.Spec, json, arg["--param=".Length..]);
                }
                else if (arg == "--timeout" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var secs) && secs > 0)
                {
                    timeoutSeconds = secs;
                    i++;
                }
            }
        }

        var validationErrors = scenario.Spec.Validate(json);
        if (validationErrors.Count > 0)
        {
            Console.Error.WriteLine($"Parameter validation failed for '{scenario.Name}':");
            foreach (var err in validationErrors)
                Console.Error.WriteLine($"  - {err}");
            return 1;
        }

        var bound = scenario.Spec.Bind(json);
        scenario.Spec.ResolveSecrets(bound);

        var calls = new ConcurrentQueue<RecordedCall>();
        var visits = new ConcurrentQueue<RecordedStepVisit>();

        using var inner = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        using var recordingHandler = new RecordingHttpHandler(inner);
        using var http = new HttpClient(recordingHandler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Beesharp/1.0");

        var scratch = new RunData(1);
        var state = scratch.EntryBagFor(0, new DataWriter(DataWriterKind.Bee, 0, 0));
        var runBag = scratch.RunBagFor(new DataWriter(DataWriterKind.Bee, 0, 0));

        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds is { } t)
            execCts.CancelAfter(TimeSpan.FromSeconds(t));

        Console.WriteLine($"=== Beesharp Debug Run: {scenario.Name} ({scenario.Id}) ===");

        static void WriteLog(RecordedLog l)
        {
            var tag = l.Level switch
            {
                "Trace" => "TRC",
                "Debug" => "DBG",
                "Warning" => "WRN",
                "Error" or "Critical" => "ERR",
                _ => "INF",
            };
            var ts = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var msg = string.IsNullOrEmpty(l.Exception) ? l.Message : $"{l.Message}\n{l.Exception}";
            Console.WriteLine($"[{ts} {tag}] {msg}");
        }

        var startupSw = Stopwatch.StartNew();
        var execSw = new Stopwatch();
        var teardownSw = new Stopwatch();
        ScenarioOutcome outcome;
        var started = false;

        await using var bgHost = new ScenarioBackgroundHost(execCts.Token);
        try
        {
            var startupCtx = new ScenarioContext(http, EnvironmentValues.None, execCts.Token, state, bgHost,
                structuredLogSink: WriteLog, runBag: runBag);
            await scenario.StartupAsync(bound, startupCtx, execCts.Token);
            startupSw.Stop();
            started = true;

            execSw.Start();
            var execCtx = new ScenarioContext(
                http,
                EnvironmentValues.None,
                execCts.Token,
                state,
                structuredLogSink: WriteLog,
                callSink: calls.Enqueue,
                runBag: runBag,
                stepVisitSink: v =>
                {
                    visits.Enqueue(v);
                    if (v.Step >= 0 && v.Step < scenario.Steps.Count)
                    {
                        var repSuffix = v.Rep > 0 ? $" (rep {v.Rep + 1})" : "";
                        var dataSuffix = string.IsNullOrEmpty(v.Data) ? "" : $" {v.Data}";
                        Console.WriteLine($"  --> Step {v.Step + 1}/{scenario.Steps.Count}: {scenario.Steps[v.Step].Name}{repSuffix}{dataSuffix}");
                    }
                });

            using (execCtx.Activate())
                outcome = await scenario.ExecuteAsync(bound, execCtx, execCts.Token);
            execSw.Stop();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutSeconds is { } secs)
        {
            startupSw.Stop();
            execSw.Stop();
            outcome = ScenarioOutcome.Failed($"Timeout after {secs} s");
        }
        catch (Exception ex)
        {
            startupSw.Stop();
            execSw.Stop();
            var msg = ex.InnerException?.Message ?? ex.Message;
            var formatted = ScenarioExceptionFormatter.Format(ex, scenario.GetType().Assembly);
            WriteLog(new RecordedLog(execSw.Elapsed.TotalSeconds, "Error", $"Unhandled exception: {msg}", formatted));
            outcome = ScenarioOutcome.Failed(msg);
        }
        finally
        {
            await bgHost.DisposeAsync();
            if (started)
            {
                teardownSw.Start();
                try
                {
                    var teardownCtx = new ScenarioContext(http, EnvironmentValues.None, CancellationToken.None, state,
                        structuredLogSink: WriteLog, runBag: runBag);
                    await scenario.TeardownAsync(bound, teardownCtx, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    var formatted = ScenarioExceptionFormatter.Format(ex, scenario.GetType().Assembly);
                    WriteLog(new RecordedLog(teardownSw.Elapsed.TotalSeconds, "Error", $"Teardown error: {ex.InnerException?.Message ?? ex.Message}", formatted));
                }
                teardownSw.Stop();
            }
        }

        var recordedCalls = calls.ToArray();
        if (recordedCalls.Length > 0)
        {
            Console.WriteLine("--- Recorded Calls ---");
            foreach (var c in recordedCalls)
            {
                var status = c.StatusCode is { } sc ? $"HTTP {sc}" : "ERR";
                var err = string.IsNullOrEmpty(c.Error) ? "" : $" ({c.Error})";
                var stepTag = scenario.Steps.Count > 0 && c.Step >= 0 && c.Step < scenario.Steps.Count
                    ? $" [{scenario.Steps[c.Step].Name}]"
                    : "";
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  +{c.OffsetMs,7:F1} ms | {c.DurationMs,7:F1} ms | {status,-8} | {c.Name}{stepTag}{err}"));
            }
        }

        var execMs = (outcome.Measured ?? execSw.Elapsed).TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"--- Outcome: {(outcome.Success ? "PASS" : $"FAIL ({outcome.FailureReason})")} | Startup: {startupSw.Elapsed.TotalMilliseconds:F1} ms | Execute: {execMs:F1} ms | Teardown: {teardownSw.Elapsed.TotalMilliseconds:F1} ms | Calls: {recordedCalls.Length} | Steps visited: {visits.Count} ---"));

        return outcome.Success ? 0 : 1;
    }

    private static void ApplyParamOverride(ParameterSpec spec, JsonObject json, string pair)
    {
        var eq = pair.IndexOf('=');
        if (eq <= 0) return;
        var key = pair[..eq].Trim();
        var value = pair[(eq + 1)..];
        var field = spec.Fields.FirstOrDefault(f =>
            string.Equals(f.JsonName, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.Property?.Name, key, StringComparison.OrdinalIgnoreCase));
        var targetKey = field?.JsonName ?? key;
        json[targetKey] = JsonValue.Create(value);
    }
}

/// <summary>Delegate-backed <see cref="IScenario"/> produced by C# scripts or inline registrations.</summary>
public sealed class DelegateScenario<TParameters> : IScenario where TParameters : class
{
    private readonly Func<TParameters, ScenarioContext, CancellationToken, Task<ScenarioOutcome>> execute;
    private readonly Func<TParameters, ScenarioContext, CancellationToken, Task>? startup;
    private readonly Func<TParameters, ScenarioContext, CancellationToken, Task>? teardown;

    public DelegateScenario(
        string id,
        string name,
        string description,
        Func<TParameters, ScenarioContext, CancellationToken, Task<ScenarioOutcome>> execute,
        Func<TParameters, ScenarioContext, CancellationToken, Task>? startup = null,
        Func<TParameters, ScenarioContext, CancellationToken, Task>? teardown = null,
        IReadOnlyList<StepInfo>? steps = null)
    {
        Id = id;
        Name = name;
        Description = description;
        Spec = ParameterSpec.For(typeof(TParameters));
        Steps = steps ?? [];
        this.execute = execute;
        this.startup = startup;
        this.teardown = teardown;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public ParameterSpec Spec { get; }
    public IReadOnlyList<StepInfo> Steps { get; }

    public Task StartupAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        startup is null ? Task.CompletedTask : startup((TParameters)parameters, context, cancellationToken);

    public Task<ScenarioOutcome> ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        execute((TParameters)parameters, context, cancellationToken);

    public Task TeardownAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        teardown is null ? Task.CompletedTask : teardown((TParameters)parameters, context, cancellationToken);
}

/// <summary>
/// Client-side proxy for a custom server-compiled scenario: its <see cref="ParameterSpec"/> is reconstructed from the
/// JSON Schema returned by <c>GET /api/scenarios</c>, so <c>ParameterEditor</c> renders the exact generated form in WASM.
/// </summary>
public sealed class RemoteSchemaScenario(string id, string name, string description, JsonObject parameterSchema, IReadOnlyList<StepInfo>? steps = null, string? scriptId = null) : IScenario
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public ParameterSpec Spec { get; } = ParameterSpec.FromJsonSchema(parameterSchema);
    public IReadOnlyList<StepInfo> Steps { get; } = steps ?? [];
    /// <summary>Script id when backed by a Roslyn script file; null for compiled <c>.csproj</c> host scenarios.</summary>
    public string? ScriptId { get; } = scriptId;

    public Task<ScenarioOutcome> ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Scenario '{Id}' is a server-side script and can only execute on the server.");
}
