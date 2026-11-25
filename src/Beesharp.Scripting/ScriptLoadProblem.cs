namespace Beesharp.Services;

/// <summary>A script file that did not compile (or register) when the scripts folder was loaded: the kind ("Scenario script", "Preflight", "Shutdown program"), the file and what is wrong.</summary>
/// <param name="Kind">The category of script that failed to load (for example, <c>"Scenario script"</c>, <c>"Preflight"</c>, or <c>"Shutdown program"</c>).</param>
/// <param name="FileName">The script file name where the load or compilation failure occurred.</param>
/// <param name="Message">A human-readable summary of the compilation errors or registration failure.</param>
public sealed record ScriptLoadProblem(string Kind, string FileName, string Message)
{
    internal static ScriptLoadProblem From(string kind, string fileName, IReadOnlyList<ScriptDiagnosticDto> diagnostics, string? error)
    {
        var errors = diagnostics.Where(d => d.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)).Take(3)
            .Select(d => $"line {d.Line}: {d.Message}").ToList();
        var text = errors.Count > 0 ? string.Join("; ", errors) : error ?? "Compilation failed.";
        return new ScriptLoadProblem(kind, fileName, text);
    }
}
