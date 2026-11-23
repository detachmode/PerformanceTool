using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios;

/// <summary>Marker parameter type for parameterless custom scripts.</summary>
public sealed class EmptyScenarioParameters;

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

/// <summary>Fluent builder returned by <see cref="ScenarioScriptHost.Define{TParameters}"/> for attaching lifecycle hooks and metadata.</summary>
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
public sealed class RemoteSchemaScenario(string id, string name, string description, JsonObject parameterSchema, IReadOnlyList<StepInfo>? steps = null) : IScenario
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public ParameterSpec Spec { get; } = ParameterSpec.FromJsonSchema(parameterSchema);
    public IReadOnlyList<StepInfo> Steps { get; } = steps ?? [];

    public Task<ScenarioOutcome> ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Scenario '{Id}' is a server-side script and can only execute on the server.");
}
