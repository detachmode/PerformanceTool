using System.Text.RegularExpressions;

namespace Beesharp.Services;

/// <summary>
/// Compatibility for user scripts written before the rename to Beesharp (brain 0054): a script that says
/// <c>using PerformanceTool.Services.Scenarios;</c> (or names such a type fully qualified) still compiles.
/// The replacement stays on its line, so diagnostics and stack traces keep their line numbers.
/// </summary>
public static partial class LegacyScriptNamespaces
{
    [GeneratedRegex(@"\bPerformanceTool(?=\.(?:Services|Ai|Client|Api)\b)")]
    private static partial Regex Legacy();

    public static string Upgrade(string code) =>
        code.Contains("PerformanceTool", StringComparison.Ordinal) ? Legacy().Replace(code, "Beesharp") : code;
}
