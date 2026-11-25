using System.Text.Json.Nodes;
using Beesharp.Ai;
using Beesharp.Services;
using Beesharp.Services.Environments;

namespace Beesharp.AiAdapters;

/// <summary>
/// Adapts <see cref="EnvironmentStore"/> to <see cref="IEnvironmentPort"/> for the AI assistant.
/// Masking secret-looking values is handled by the assistant tools rather than this adapter.
/// </summary>
/// <param name="store">The file-backed environment store.</param>
public sealed class EnvironmentAdapter(EnvironmentStore store) : IEnvironmentPort
{
    private static EnvironmentInfo Map(EnvironmentDefinition e) =>
        new(e.Name, e.Variables.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)).ToList());

    /// <summary>Lists all saved environments.</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A read-only list of all environments in the store.</returns>
    public async Task<IReadOnlyList<EnvironmentInfo>> ListAsync(CancellationToken ct) => (await store.ListAsync(ct)).Select(Map).ToList();

    /// <summary>Gets a single environment by name.</summary>
    /// <param name="name">The environment name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The matching <see cref="EnvironmentInfo"/>, or <see langword="null"/> if it does not exist.</returns>
    public async Task<EnvironmentInfo?> GetAsync(string name, CancellationToken ct) => await store.GetAsync(name, ct) is { } e ? Map(e) : null;

    /// <summary>Creates or replaces an environment definition in the store.</summary>
    /// <param name="environment">The environment name and variables to persist.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    public Task SaveAsync(EnvironmentInfo environment, CancellationToken ct) =>
        store.SaveAsync(new EnvironmentDefinition { Name = environment.Name, Variables = environment.Variables.Select(v => new EnvironmentVariable(v.Key, v.Value)).ToList() }, ct);

    /// <summary>Deletes an environment by name if it exists.</summary>
    /// <param name="name">The environment name to delete.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if the environment existed and was deleted; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        if (await store.GetAsync(name, ct) is null) return false;
        await store.DeleteAsync(name, ct);
        return true;
    }
}

/// <summary>
/// Adapts <see cref="ScriptScenarioStore"/> and <see cref="PreflightStore"/> to <see cref="IScriptPort"/> for the AI assistant.
/// </summary>
/// <param name="scenarioScripts">The store managing custom C# scenario scripts.</param>
/// <param name="preflights">The store managing custom C# preflight scripts.</param>
public sealed class ScriptAdapter(ScriptScenarioStore scenarioScripts, PreflightStore preflights) : IScriptPort
{
    private static ScriptInfo Map(ScriptScenarioSummary s) => new(s.ScriptId, s.FileName, s.ScenarioName, s.ScenarioId, s.Description, s.UpdatedUtc, s.Code);
    private static ScriptInfo Map(PreflightScriptSummary s) => new(s.ScriptId, s.FileName, s.Name, null, "", s.UpdatedUtc, s.Code);
    private static IReadOnlyList<Diagnostic> Map(IReadOnlyList<ScriptDiagnosticDto> d) => d.Select(x => new Diagnostic(x.Severity, x.Line, x.Column, x.Message)).ToList();

    /// <summary>Lists all registered custom scenario scripts.</summary>
    /// <returns>A read-only list of scenario script summaries.</returns>
    public IReadOnlyList<ScriptInfo> ListScripts() => scenarioScripts.List().Select(Map).ToList();

    /// <summary>Gets a single custom scenario script by its identifier.</summary>
    /// <param name="id">The script identifier.</param>
    /// <returns>The matching <see cref="ScriptInfo"/>, or <see langword="null"/> if not found.</returns>
    public ScriptInfo? GetScript(string id) => scenarioScripts.Get(id) is { } s ? Map(s) : null;

    /// <summary>Compiles, registers, and persists a custom C# scenario script.</summary>
    /// <param name="id">An optional existing script identifier to update, or <see langword="null"/> to create a new script.</param>
    /// <param name="fileName">An optional source file name for the script.</param>
    /// <param name="code">The C# source code to compile.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="CompileOutcome"/> containing compilation success, script metadata, diagnostics, and any error message.</returns>
    public async Task<CompileOutcome> UploadScriptAsync(string? id, string? fileName, string code, CancellationToken ct)
    {
        var r = await scenarioScripts.CompileAndRegisterAsync(id, fileName, code, persist: true, ct);
        return new CompileOutcome(r.Success, r.Script is null ? null : Map(r.Script), Map(r.Diagnostics), r.Error);
    }

    /// <summary>Executes a single test run of a compiled scenario script.</summary>
    /// <param name="id">The scenario script identifier.</param>
    /// <param name="parameters">Optional JSON parameters for the scenario, or <see langword="null"/> to use defaults.</param>
    /// <param name="environmentName">Optional environment name whose variables are supplied to the script.</param>
    /// <param name="timeoutSeconds">Maximum execution time in seconds before canceling the test run.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="ScriptTestOutcome"/> containing status, duration, recorded HTTP calls, and logs.</returns>
    public async Task<ScriptTestOutcome> TestRunScriptAsync(string id, JsonObject? parameters, string? environmentName, int timeoutSeconds, CancellationToken ct)
    {
        var r = await scenarioScripts.TestRunAsync(id, parameters, environmentName, timeoutSeconds, ct);
        return new ScriptTestOutcome(r.Success, r.StatusCode, r.DurationMs, r.Error,
            r.Calls.Select(c => new TestCallInfo(c.Method, c.Url, c.StatusCode, c.DurationMs, c.Error)).ToList(), r.Logs);
    }

    /// <summary>Deletes a custom scenario script by its identifier.</summary>
    /// <param name="id">The scenario script identifier.</param>
    /// <returns><see langword="true"/> if the script existed and was deleted; otherwise, <see langword="false"/>.</returns>
    public bool DeleteScript(string id) => scenarioScripts.Delete(id);

    /// <summary>Lists all registered preflight scripts.</summary>
    /// <returns>A read-only list of preflight script summaries.</returns>
    public IReadOnlyList<ScriptInfo> ListPreflights() => preflights.List().Select(Map).ToList();

    /// <summary>Gets a single preflight script by its identifier.</summary>
    /// <param name="id">The preflight script identifier.</param>
    /// <returns>The matching <see cref="ScriptInfo"/>, or <see langword="null"/> if not found.</returns>
    public ScriptInfo? GetPreflight(string id) => preflights.Get(id) is { } s ? Map(s) : null;

    /// <summary>Compiles and persists a custom C# preflight script.</summary>
    /// <param name="id">An optional existing preflight identifier to update, or <see langword="null"/> to create a new script.</param>
    /// <param name="fileName">An optional source file name for the script.</param>
    /// <param name="code">The C# source code to compile.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="CompileOutcome"/> containing compilation success, script metadata, diagnostics, and any error message.</returns>
    public async Task<CompileOutcome> UploadPreflightAsync(string? id, string? fileName, string code, CancellationToken ct)
    {
        var r = await preflights.CompileAndSaveAsync(id, fileName, code, persist: true, ct);
        return new CompileOutcome(r.Success, r.Script is null ? null : Map(r.Script), Map(r.Diagnostics), r.Error);
    }

    /// <summary>Executes a single test run of a compiled preflight script.</summary>
    /// <param name="id">The preflight script identifier.</param>
    /// <param name="environmentName">Optional environment name whose variables are supplied to the preflight script.</param>
    /// <param name="timeoutSeconds">Maximum execution time in seconds before canceling the execution.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="PreflightTestOutcome"/> containing success, duration, error message, and logs.</returns>
    public async Task<PreflightTestOutcome> TestRunPreflightAsync(string id, string? environmentName, int timeoutSeconds, CancellationToken ct)
    {
        var r = await preflights.ExecuteAsync(id, environmentName, timeoutSeconds: timeoutSeconds, ct: ct);
        return new PreflightTestOutcome(r.Success, r.DurationMs, r.Error, r.Logs);
    }

    /// <summary>Deletes a preflight script by its identifier.</summary>
    /// <param name="id">The preflight script identifier.</param>
    /// <returns><see langword="true"/> if the preflight script existed and was deleted; otherwise, <see langword="false"/>.</returns>
    public bool DeletePreflight(string id) => preflights.Delete(id);
}
