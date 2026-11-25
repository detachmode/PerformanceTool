using System.ComponentModel;
using System.Text.Json.Nodes;
using Beesharp.Ai.Contracts;

namespace Beesharp.Ai;

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

    /// <summary>Lists all configured environments with secret-looking variable values masked.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of environment summaries containing names and masked key-value pairs.</returns>
    [AssistantTool("list_environments")]
    [Description("Lists the environments (named sets of key-value pairs such as BaseUrl or Token that scenarios run against) with their keys. Values of secret-looking keys are masked.")]
    public async Task<object> ListEnvironments(CancellationToken ct)
    {
        var all = await environments.ListAsync(ct);
        rec.Read($"Looked at the {all.Count} environments", EnvironmentsLink);
        return all.Select(EnvView).ToList();
    }

    /// <summary>Reads a single environment by name with secret-looking variable values masked.</summary>
    /// <param name="name">Name of the environment to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An object containing the environment name and masked variables.</returns>
    [AssistantTool("get_environment")]
    [Description("Reads one environment. Values of secret-looking keys (token, password, key, ...) are masked and never shown in full.")]
    public async Task<object> GetEnvironment([Description("Environment name")] string name, CancellationToken ct)
    {
        var e = await environments.GetAsync(name, ct) ?? throw new KeyNotFoundException($"There is no environment '{name}'. Use list_environments.");
        rec.Read($"Looked at environment {e.Name}", EnvironmentsLink);
        return EnvView(e);
    }

    /// <summary>Creates or updates an environment by merging new variable values and removing deleted keys.</summary>
    /// <param name="name">Name of the environment to create or update.</param>
    /// <param name="variables">Optional map of keys to add or overwrite; masked values preserve existing secrets.</param>
    /// <param name="removeKeys">Optional list of variable keys to delete from the environment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The saved environment view with secret-looking values masked.</returns>
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

    /// <summary>Deletes a named environment permanently.</summary>
    /// <param name="name">Name of the environment to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An object confirming the deleted environment name.</returns>
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

    /// <summary>Lists all custom C# scenario scripts uploaded to the server.</summary>
    /// <returns>A list of brief metadata objects describing each scenario script.</returns>
    [AssistantTool("list_scripts")]
    [Description("Lists the custom C# scenario scripts uploaded to the server (each defines a scenario).")]
    public object ListScripts()
    {
        var list = scripts.ListScripts();
        rec.Read($"Looked at the {list.Count} custom scripts", ScriptsLink);
        return list.Select(ScriptBrief).ToList();
    }

    /// <summary>Reads a custom scenario script including its C# source code (bounded to 20,000 characters).</summary>
    /// <param name="scriptId">Identifier of the script to read.</param>
    /// <returns>An object containing the script metadata and bounded C# code.</returns>
    [AssistantTool("get_script")]
    [Description("Reads a custom scenario script with its C# code (cut at 20000 characters).")]
    public object GetScript([Description("Script id from list_scripts")] string scriptId)
    {
        var s = scripts.GetScript(scriptId) ?? throw new KeyNotFoundException($"There is no script '{scriptId}'. Use list_scripts.");
        rec.Read($"Read script {s.FileName}", ScriptsLink);
        return ScriptFull(s);
    }

    /// <summary>Compiles and saves a custom C# scenario script, registering or updating its scenario in the catalog.</summary>
    /// <param name="code">C# source code of the scenario script.</param>
    /// <param name="fileName">Optional file name for the script (for example <c>checkout-flow.cs</c>).</param>
    /// <param name="scriptId">Optional existing script id to replace; omit to create a new script.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compilation outcome including script metadata on success or bounded diagnostics on failure.</returns>
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

    /// <summary>Executes a single test bee of a custom scenario script outside of any load run.</summary>
    /// <param name="scriptId">Identifier of the scenario script to test.</param>
    /// <param name="parameters">Optional JSON object of scenario parameters; defaults are used when omitted.</param>
    /// <param name="environmentName">Optional environment name supplying placeholder values.</param>
    /// <param name="timeoutSeconds">Optional timeout in seconds (clamped between 1 and 300; defaults to 30).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Test execution status, duration, recorded calls, and log lines.</returns>
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

    /// <summary>Permanently deletes a custom scenario script and removes its scenario from the catalog.</summary>
    /// <param name="scriptId">Identifier of the script to delete.</param>
    /// <returns>An object confirming the deleted script id.</returns>
    [AssistantTool("delete_script")]
    [Description("Deletes a custom scenario script for good (its scenario leaves the catalog).")]
    public object DeleteScript([Description("Script id from list_scripts")] string scriptId)
    {
        if (!scripts.DeleteScript(scriptId)) throw new KeyNotFoundException($"There is no script '{scriptId}'. Use list_scripts.");
        rec.Change(ChangedAreas.Scripts, $"Deleted script {scriptId}", ScriptsLink);
        return new { deleted = scriptId };
    }

    // ---------- preflights ----------

    /// <summary>Lists all preflight scripts available on the server.</summary>
    /// <returns>A list of brief metadata objects describing each preflight script.</returns>
    [AssistantTool("list_preflights")]
    [Description("Lists the preflight scripts. A preflight runs before a run's scenarios start; the scenarios only begin when it succeeds.")]
    public object ListPreflights()
    {
        var list = scripts.ListPreflights();
        rec.Read($"Looked at the {list.Count} preflights", ScriptsLink);
        return list.Select(ScriptBrief).ToList();
    }

    /// <summary>Reads a preflight script including its C# source code (bounded to 20,000 characters).</summary>
    /// <param name="scriptId">Identifier of the preflight script to read.</param>
    /// <returns>An object containing the preflight metadata and bounded C# code.</returns>
    [AssistantTool("get_preflight")]
    [Description("Reads a preflight script with its C# code (cut at 20000 characters).")]
    public object GetPreflight([Description("Preflight id from list_preflights")] string scriptId)
    {
        var s = scripts.GetPreflight(scriptId) ?? throw new KeyNotFoundException($"There is no preflight '{scriptId}'. Use list_preflights.");
        rec.Read($"Read preflight {s.FileName}", ScriptsLink);
        return ScriptFull(s);
    }

    /// <summary>Compiles and saves a preflight C# script on the server.</summary>
    /// <param name="code">C# source code of the preflight script.</param>
    /// <param name="fileName">Optional file name for the script (for example <c>check-readiness.cs</c>).</param>
    /// <param name="scriptId">Optional existing preflight id to replace; omit to create a new preflight.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compilation outcome including preflight metadata on success or bounded diagnostics on failure.</returns>
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

    /// <summary>Executes a preflight script once outside of any load run.</summary>
    /// <param name="scriptId">Identifier of the preflight script to test.</param>
    /// <param name="environmentName">Optional environment name supplying placeholder values.</param>
    /// <param name="timeoutSeconds">Optional timeout in seconds (clamped between 1 and 300; defaults to 60).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preflight test outcome containing success flag, duration, error, and log lines.</returns>
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

    /// <summary>Permanently deletes a preflight script.</summary>
    /// <param name="scriptId">Identifier of the preflight script to delete.</param>
    /// <returns>An object confirming the deleted preflight id.</returns>
    [AssistantTool("delete_preflight")]
    [Description("Deletes a preflight script for good.")]
    public object DeletePreflight([Description("Preflight id from list_preflights")] string scriptId)
    {
        if (!scripts.DeletePreflight(scriptId)) throw new KeyNotFoundException($"There is no preflight '{scriptId}'. Use list_preflights.");
        rec.Change(ChangedAreas.Preflights, $"Deleted preflight {scriptId}", ScriptsLink);
        return new { deleted = scriptId };
    }
}
