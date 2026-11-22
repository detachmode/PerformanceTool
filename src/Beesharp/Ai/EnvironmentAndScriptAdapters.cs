using System.Text.Json.Nodes;
using Beesharp.Ai;
using Beesharp.Services;
using Beesharp.Services.Environments;

namespace Beesharp.AiAdapters;

/// <summary><see cref="IEnvironmentPort"/> over <see cref="EnvironmentStore"/>. Masking secret values is the tools' job, not the port's.</summary>
public sealed class EnvironmentAdapter(EnvironmentStore store) : IEnvironmentPort
{
    private static EnvironmentInfo Map(EnvironmentDefinition e) =>
        new(e.Name, e.Variables.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)).ToList());

    public async Task<IReadOnlyList<EnvironmentInfo>> ListAsync(CancellationToken ct) => (await store.ListAsync(ct)).Select(Map).ToList();

    public async Task<EnvironmentInfo?> GetAsync(string name, CancellationToken ct) => await store.GetAsync(name, ct) is { } e ? Map(e) : null;

    public Task SaveAsync(EnvironmentInfo environment, CancellationToken ct) =>
        store.SaveAsync(new EnvironmentDefinition { Name = environment.Name, Variables = environment.Variables.Select(v => new EnvironmentVariable(v.Key, v.Value)).ToList() }, ct);

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        if (await store.GetAsync(name, ct) is null) return false;
        await store.DeleteAsync(name, ct);
        return true;
    }
}

/// <summary><see cref="IScriptPort"/> over <see cref="ScriptScenarioStore"/> (scenario scripts) and <see cref="PreflightStore"/>.</summary>
public sealed class ScriptAdapter(ScriptScenarioStore scenarioScripts, PreflightStore preflights) : IScriptPort
{
    private static ScriptInfo Map(ScriptScenarioSummary s) => new(s.ScriptId, s.FileName, s.ScenarioName, s.ScenarioId, s.Description, s.UpdatedUtc, s.Code);
    private static ScriptInfo Map(PreflightScriptSummary s) => new(s.ScriptId, s.FileName, s.Name, null, "", s.UpdatedUtc, s.Code);
    private static IReadOnlyList<Diagnostic> Map(IReadOnlyList<ScriptDiagnosticDto> d) => d.Select(x => new Diagnostic(x.Severity, x.Line, x.Column, x.Message)).ToList();

    public IReadOnlyList<ScriptInfo> ListScripts() => scenarioScripts.List().Select(Map).ToList();
    public ScriptInfo? GetScript(string id) => scenarioScripts.Get(id) is { } s ? Map(s) : null;

    public async Task<CompileOutcome> UploadScriptAsync(string? id, string? fileName, string code, CancellationToken ct)
    {
        var r = await scenarioScripts.CompileAndRegisterAsync(id, fileName, code, persist: true, ct);
        return new CompileOutcome(r.Success, r.Script is null ? null : Map(r.Script), Map(r.Diagnostics), r.Error);
    }

    public async Task<ScriptTestOutcome> TestRunScriptAsync(string id, JsonObject? parameters, string? environmentName, int timeoutSeconds, CancellationToken ct)
    {
        var r = await scenarioScripts.TestRunAsync(id, parameters, environmentName, timeoutSeconds, ct);
        return new ScriptTestOutcome(r.Success, r.StatusCode, r.DurationMs, r.Error,
            r.Calls.Select(c => new TestCallInfo(c.Method, c.Url, c.StatusCode, c.DurationMs, c.Error)).ToList(), r.Logs);
    }

    public bool DeleteScript(string id) => scenarioScripts.Delete(id);

    public IReadOnlyList<ScriptInfo> ListPreflights() => preflights.List().Select(Map).ToList();
    public ScriptInfo? GetPreflight(string id) => preflights.Get(id) is { } s ? Map(s) : null;

    public async Task<CompileOutcome> UploadPreflightAsync(string? id, string? fileName, string code, CancellationToken ct)
    {
        var r = await preflights.CompileAndSaveAsync(id, fileName, code, persist: true, ct);
        return new CompileOutcome(r.Success, r.Script is null ? null : Map(r.Script), Map(r.Diagnostics), r.Error);
    }

    public async Task<PreflightTestOutcome> TestRunPreflightAsync(string id, string? environmentName, int timeoutSeconds, CancellationToken ct)
    {
        var r = await preflights.ExecuteAsync(id, environmentName, timeoutSeconds: timeoutSeconds, ct: ct);
        return new PreflightTestOutcome(r.Success, r.DurationMs, r.Error, r.Logs);
    }

    public bool DeletePreflight(string id) => preflights.Delete(id);
}
