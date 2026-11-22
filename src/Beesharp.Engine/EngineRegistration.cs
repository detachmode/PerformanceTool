using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Services;

public static class EngineRegistration
{
    /// <summary>
    /// Registers the runner and everything it needs except the run storage (brain 0054): the scenario catalog (the scenarios themselves come from
    /// <c>AddScenario&lt;T&gt;()</c>), the preset and environment stores, the script stores (Scripting), <see cref="LoadRunner"/>, <see cref="PlanWorkspace"/>
    /// and <see cref="RunManager"/>. The caller adds an <see cref="Storage.IRunStorage"/> (<c>AddBeesharpStorage</c>). No ASP.NET: a command-line tool calls this too.
    /// </summary>
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
    /// after the container is built and before the first run.
    /// </summary>
    public static async Task LoadBeesharpScriptsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await services.GetRequiredService<ScriptScenarioStore>().LoadAllFromDiskAsync(ct);
        await services.GetRequiredService<PreflightStore>().LoadAllFromDiskAsync(ct);
        await services.GetRequiredService<ShutdownStore>().LoadAllFromDiskAsync(ct);
    }
}
