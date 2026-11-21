using System.ComponentModel;
using System.Text.Json.Nodes;
using PerformanceTool.Ai.Contracts;

namespace PerformanceTool.Ai;

// Environments, custom scenario scripts and preflights.
public sealed partial class AssistantToolbox
{
    private static readonly ChipLinkDto EnvironmentsLink = new("environments");
    private static readonly ChipLinkDto ScriptsLink = new("scripts");

    private static object EnvView(EnvironmentInfo e) => new
    {
        name = e.Name,
        variables = e.Variables.ToDictionary(v => v.Key, v => SecretMask.Mask(v.Key, v.Value)),
        note = e.Variables.Any(v => SecretMask.LooksSecret(v.Key)) ? "Values of secret-looking keys are masked." : null,
    };

    [AssistantTool("list_environments")]
    [Description("Lists the environments (named sets of key-value pairs such as BaseUrl or Token that scenarios run against) with their keys. Values of secret-looking keys are masked.")]
    public async Task<object> ListEnvironments(CancellationToken ct)
    {
        var all = await environments.ListAsync(ct);
        rec.Read($"Looked at the {all.Count} environments", EnvironmentsLink);
        return all.Select(EnvView).ToList();
    }

    [AssistantTool("get_environment")]
    [Description("Reads one environment. Values of secret-looking keys (token, password, key, ...) are masked and never shown in full.")]
    public async Task<object> GetEnvironment([Description("Environment name")] string name, CancellationToken ct)
    {
        var e = await environments.GetAsync(name, ct) ?? throw new KeyNotFoundException($"There is no environment '{name}'. Use list_environments.");
        rec.Read($"Looked at environment {e.Name}", EnvironmentsLink);
        return EnvView(e);
    }

    [AssistantTool("save_environment")]
    [Description("Creates an environment or changes one: the given keys are set (added or replaced), the other keys stay, and removeKeys are dropped. A value that is still masked (contains ••••) keeps the stored value.")]
    public async Task<object> SaveEnvironment(
        [Description("Environment name")] string name,
        [Description("Keys to set, as a JSON object of key to value, e.g. {\"BaseUrl\":\"https://staging.example.com\"}")] Dictionary<string, string>? variables = null,
        [Description("Keys to remove")] List<string>? removeKeys = null,
        CancellationToken ct = default)
    {
        var existing = await environments.GetAsync(name, ct);
        var map = new List<KeyValuePair<string, string>>(existing?.Variables ?? []);
        foreach (var (key, value) in variables ?? [])
        {
            var i = map.FindIndex(p => p.Key == key);
            if (i >= 0)
            {
                if (!SecretMask.IsMasked(value)) map[i] = new(key, value);
            }
            else map.Add(new(key, value));
        }
        foreach (var key in removeKeys ?? []) map.RemoveAll(p => p.Key == key);
        var saved = new EnvironmentInfo(name.Trim(), map);
        await environments.SaveAsync(saved, ct);
        rec.Change(ChangedAreas.Environments, existing is null ? $"Created environment {saved.Name}" : $"Changed environment {saved.Name}", EnvironmentsLink);
        return EnvView(saved);
    }

    [AssistantTool("delete_environment")]
    [Description("Deletes an environment for good.")]
    public async Task<object> DeleteEnvironment([Description("Environment name")] string name, CancellationToken ct)
    {
        if (!await environments.DeleteAsync(name, ct)) throw new KeyNotFoundException($"There is no environment '{name}'. Use list_environments.");
        rec.Change(ChangedAreas.Environments, $"Deleted environment {name.Trim()}", EnvironmentsLink);
        return new { deleted = name.Trim() };
    }

    // ---------- custom scenario scripts ----------

    private static object ScriptBrief(ScriptInfo s) => new { scriptId = s.ScriptId, fileName = s.FileName, name = s.Name, scenarioId = s.ScenarioId, description = s.Description, updatedUtc = s.UpdatedUtc };

    private static object ScriptFull(ScriptInfo s) => new
    {
        scriptId = s.ScriptId, fileName = s.FileName, name = s.Name, scenarioId = s.ScenarioId, description = s.Description, updatedUtc = s.UpdatedUtc,
        code = Bounds.Cut(s.Code, Bounds.MaxScriptChars), codeCut = s.Code.Length > Bounds.MaxScriptChars,
    };

    private static object CompileView(CompileOutcome r) => new
    {
        success = r.Success,
        error = r.Error,
        script = r.Script is null ? null : ScriptBrief(r.Script),
        diagnostics = r.Diagnostics.Take(Bounds.DefaultList).Select(d => new { d.Severity, d.Line, d.Column, d.Message }).ToList(),
    };

    [AssistantTool("list_scripts")]
    [Description("Lists the custom C# scenario scripts uploaded to the server (each defines a scenario).")]
    public object ListScripts()
    {
        var list = scripts.ListScripts();
        rec.Read($"Looked at the {list.Count} custom scripts", ScriptsLink);
        return list.Select(ScriptBrief).ToList();
    }

    [AssistantTool("get_script")]
    [Description("Reads a custom scenario script with its C# code (cut at 20000 characters).")]
    public object GetScript([Description("Script id from list_scripts")] string scriptId)
    {
        var s = scripts.GetScript(scriptId) ?? throw new KeyNotFoundException($"There is no script '{scriptId}'. Use list_scripts.");
        rec.Read($"Read script {s.FileName}", ScriptsLink);
        return ScriptFull(s);
    }

    [AssistantTool("upload_script")]
    [Description("Compiles C# scenario script code and saves it on the server; the scenario it defines is added to the catalog. An existing script id replaces that script. Returns compile problems when it does not compile.")]
    public async Task<object> UploadScript(
        [Description("The C# code of the script")] string code,
        [Description("File name, e.g. checkout-flow.cs")] string? fileName = null,
        [Description("Id of the script to replace; leave out for a new script")] string? scriptId = null,
        CancellationToken ct = default)
    {
        var result = await scripts.UploadScriptAsync(scriptId, fileName, code, ct);
        if (result.Success) rec.Change(ChangedAreas.Scripts, $"Uploaded script {result.Script?.FileName ?? fileName ?? "script"}", ScriptsLink);
        else rec.Read($"Script did not compile ({result.Diagnostics.Count} problems)", ScriptsLink);
        return CompileView(result);
    }

    [AssistantTool("test_run_script")]
    [Description("Runs one bee of a custom scenario script once, outside any run, and returns status, timing, calls and logs. Safe way to check a script before adding it to a plan.")]
    public async Task<object> TestRunScript(
        [Description("Script id from list_scripts")] string scriptId,
        [Description("Scenario parameters for the test, as a JSON object; defaults otherwise")] JsonObject? parameters = null,
        [Description("Environment to run against")] string? environmentName = null,
        [Description("Timeout in seconds (default 30)")] int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        var r = await scripts.TestRunScriptAsync(scriptId, parameters, string.IsNullOrWhiteSpace(environmentName) ? null : environmentName.Trim(), Math.Clamp(timeoutSeconds ?? 30, 1, 300), ct);
        rec.Read(r.Success ? $"Test-ran script {scriptId}: ok in {r.DurationMs:0} ms" : $"Test-ran script {scriptId}: failed", ScriptsLink);
        return new
        {
            success = r.Success, status = r.StatusCode, durationMs = Math.Round(r.DurationMs), error = r.Error is null ? null : Bounds.Cut(r.Error),
            calls = r.Calls.Take(Bounds.DefaultList).Select(c => new { c.Method, c.Url, status = c.StatusCode, durationMs = Math.Round(c.DurationMs), error = c.Error }).ToList(),
            logs = r.Logs.Take(Bounds.DefaultList).Select(l => Bounds.Cut(l)).ToList(),
        };
    }

    [AssistantTool("delete_script")]
    [Description("Deletes a custom scenario script for good (its scenario leaves the catalog).")]
    public object DeleteScript([Description("Script id from list_scripts")] string scriptId)
    {
        if (!scripts.DeleteScript(scriptId)) throw new KeyNotFoundException($"There is no script '{scriptId}'. Use list_scripts.");
        rec.Change(ChangedAreas.Scripts, $"Deleted script {scriptId}", ScriptsLink);
        return new { deleted = scriptId };
    }

    // ---------- preflights ----------

    [AssistantTool("list_preflights")]
    [Description("Lists the preflight scripts. A preflight runs before a run's scenarios start; the scenarios only begin when it succeeds.")]
    public object ListPreflights()
    {
        var list = scripts.ListPreflights();
        rec.Read($"Looked at the {list.Count} preflights", ScriptsLink);
        return list.Select(ScriptBrief).ToList();
    }

    [AssistantTool("get_preflight")]
    [Description("Reads a preflight script with its C# code (cut at 20000 characters).")]
    public object GetPreflight([Description("Preflight id from list_preflights")] string scriptId)
    {
        var s = scripts.GetPreflight(scriptId) ?? throw new KeyNotFoundException($"There is no preflight '{scriptId}'. Use list_preflights.");
        rec.Read($"Read preflight {s.FileName}", ScriptsLink);
        return ScriptFull(s);
    }

    [AssistantTool("upload_preflight")]
    [Description("Compiles preflight C# code and saves it on the server. An existing preflight id replaces that preflight.")]
    public async Task<object> UploadPreflight(
        [Description("The C# code of the preflight")] string code,
        [Description("File name, e.g. check-readiness.cs")] string? fileName = null,
        [Description("Id of the preflight to replace; leave out for a new one")] string? scriptId = null,
        CancellationToken ct = default)
    {
        var result = await scripts.UploadPreflightAsync(scriptId, fileName, code, ct);
        if (result.Success) rec.Change(ChangedAreas.Preflights, $"Uploaded preflight {result.Script?.FileName ?? fileName ?? "preflight"}", ScriptsLink);
        else rec.Read($"Preflight did not compile ({result.Diagnostics.Count} problems)", ScriptsLink);
        return CompileView(result);
    }

    [AssistantTool("test_run_preflight")]
    [Description("Runs a preflight once, outside any run, and returns whether it succeeded plus its log lines.")]
    public async Task<object> TestRunPreflight(
        [Description("Preflight id from list_preflights")] string scriptId,
        [Description("Environment to run against")] string? environmentName = null,
        [Description("Timeout in seconds (default 60)")] int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        var r = await scripts.TestRunPreflightAsync(scriptId, string.IsNullOrWhiteSpace(environmentName) ? null : environmentName.Trim(), Math.Clamp(timeoutSeconds ?? 60, 1, 300), ct);
        rec.Read(r.Success ? $"Test-ran preflight {scriptId}: ok" : $"Test-ran preflight {scriptId}: failed", ScriptsLink);
        return new { success = r.Success, durationMs = Math.Round(r.DurationMs), error = r.Error is null ? null : Bounds.Cut(r.Error), logs = r.Logs.Take(Bounds.MaxList).Select(l => Bounds.Cut(l)).ToList() };
    }

    [AssistantTool("delete_preflight")]
    [Description("Deletes a preflight script for good.")]
    public object DeletePreflight([Description("Preflight id from list_preflights")] string scriptId)
    {
        if (!scripts.DeletePreflight(scriptId)) throw new KeyNotFoundException($"There is no preflight '{scriptId}'. Use list_preflights.");
        rec.Change(ChangedAreas.Preflights, $"Deleted preflight {scriptId}", ScriptsLink);
        return new { deleted = scriptId };
    }
}
