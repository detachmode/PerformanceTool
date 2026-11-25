using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Services;

/// <summary>
/// Dependency-injection registration and script-loading helpers for the Beesharp engine.
/// </summary>
public static class EngineRegistration
{
    /// <summary>
    /// Registers the runner and everything it needs except the run storage (brain 0054): the scenario catalog (the scenarios themselves come from
    /// <c>AddScenario&lt;T&gt;()</c>), the preset and environment stores, the script stores (Scripting), <see cref="LoadRunner"/>, <see cref="PlanWorkspace"/>
    /// and <see cref="RunManager"/>. The caller adds an <see cref="Storage.IRunStorage"/> (<c>AddBeesharpStorage</c>). No ASP.NET: a command-line tool calls this too.
    /// </summary>
    /// <param name="services">The service collection to register engine services into.</param>
    /// <param name="directories">Optional directory configuration for presets, environments, and scripts; defaults to <see cref="BeesharpDirectories"/> defaults when null.</param>
    /// <returns>The updated <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddBeesharpEngine(this IServiceCollection services, BeesharpDirectories? directories = null)
    {
        services.AddBeesharpFileStores(directories ?? new BeesharpDirectories());
        services.AddSingleton<Scenarios.ScenarioCatalog>();
        services.AddBeesharpScripting();
        services.AddSingleton<LoadRunner>();
        services.AddSingleton<PlanWorkspace>();
        services.AddSingleton<RunManager>();
        return services;
    }

    /// <summary>
    /// Compiles the scripts in the scripts folder and registers their scenarios, preflights and shutdown programs. Call it once at startup,
    /// after the container is built and before the first run. Returns the files that did not compile (the host ignores them, the command-line tool reports them).
    /// </summary>
    /// <param name="services">The built service provider containing the script stores and directory configuration.</param>
    /// <param name="ct">A token to cancel script discovery and compilation.</param>
    /// <returns>A read-only list of compilation or load problems encountered while loading scripts from disk.</returns>
    public static async Task<IReadOnlyList<ScriptLoadProblem>> LoadBeesharpScriptsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ScriptSdkDropper.Sync(services.GetRequiredService<BeesharpDirectories>().ResolveScripts());
        var problems = new List<ScriptLoadProblem>();
        problems.AddRange(await services.GetRequiredService<ScriptScenarioStore>().LoadAllFromDiskAsync(ct));
        problems.AddRange(await services.GetRequiredService<PreflightStore>().LoadAllFromDiskAsync(ct));
        problems.AddRange(await services.GetRequiredService<ShutdownStore>().LoadAllFromDiskAsync(ct));
        return problems;
    }
}
