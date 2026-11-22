using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace PerformanceTool.Services;

/// <summary>
/// Formats an exception thrown by a scenario or preflight script so the stack trace only shows frames
/// from that scenario's own assembly (custom script or DLL), hiding Beesharp runner and runtime plumbing.
/// </summary>
public static class ScenarioExceptionFormatter
{
    public static string Format(Exception ex, Assembly? scenarioAssembly)
    {
        var sb = new StringBuilder();
        AppendException(sb, ex, scenarioAssembly, depth: 0);
        return sb.ToString().TrimEnd();
    }

    private static void AppendException(StringBuilder sb, Exception ex, Assembly? scenarioAssembly, int depth)
    {
        if (depth > 0) sb.Append(" ---> ");
        sb.Append(ex.GetType().FullName ?? ex.GetType().Name);
        if (!string.IsNullOrEmpty(ex.Message))
        {
            sb.Append(": ");
            sb.Append(ex.Message);
        }

        if (ex.InnerException is not null)
        {
            sb.AppendLine();
            AppendException(sb, ex.InnerException, scenarioAssembly, depth + 1);
            sb.AppendLine();
            sb.Append("   --- End of inner exception stack trace ---");
        }

        var frames = ExtractFrames(ex, scenarioAssembly);
        foreach (var line in frames)
        {
            sb.AppendLine();
            sb.Append("   at ");
            sb.Append(line);
        }
    }

    private static List<string> ExtractFrames(Exception ex, Assembly? scenarioAssembly)
    {
        var st = new StackTrace(ex, fNeedFileInfo: true);
        var all = st.GetFrames();
        var filtered = new List<string>();

        if (scenarioAssembly is not null)
        {
            foreach (var f in all)
            {
                var m = f.GetMethod();
                var asm = m?.DeclaringType?.Assembly;
                if (asm == scenarioAssembly)
                    filtered.Add(FormatFrame(f, m!));
            }
        }

        if (filtered.Count > 0)
            return filtered;

        // Fallback if the exception originated inside a BCL call without user frames on this exception:
        // exclude Beesharp host/runner plumbing and async state-machine internals.
        foreach (var f in all)
        {
            var m = f.GetMethod();
            var ns = m?.DeclaringType?.Namespace ?? "";
            var full = m?.DeclaringType?.FullName ?? "";
            if (full.StartsWith("PerformanceTool.Services.LoadRunner", StringComparison.Ordinal)
                || full.StartsWith("PerformanceTool.Services.ScriptScenarioStore", StringComparison.Ordinal)
                || full.StartsWith("PerformanceTool.Services.PreflightStore", StringComparison.Ordinal)
                || full.StartsWith("PerformanceTool.Services.Scenarios.Scenario`", StringComparison.Ordinal)
                || ns.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal)
                || ns.StartsWith("System.Runtime.ExceptionServices", StringComparison.Ordinal)
                || ns.StartsWith("System.Threading.Tasks", StringComparison.Ordinal))
                continue;

            if (m is not null)
                filtered.Add(FormatFrame(f, m));
        }

        return filtered;
    }

    private static string FormatFrame(StackFrame frame, MethodBase method)
    {
        var type = method.DeclaringType;
        var typeName = type?.FullName ?? "";
        var methodName = method.Name;

        // Clean up compiler-generated async state machine names like "MyScenario+<ExecuteAsync>d__4.MoveNext"
        if (methodName == "MoveNext" && type is not null && type.Name.StartsWith('<'))
        {
            var close = type.Name.IndexOf('>');
            if (close > 1 && type.DeclaringType is { } outer)
            {
                typeName = outer.FullName ?? outer.Name;
                methodName = type.Name[1..close];
            }
        }

        var sb = new StringBuilder();
        if (typeName.Length > 0)
        {
            sb.Append(typeName);
            sb.Append('.');
        }
        sb.Append(methodName);
        sb.Append('(');
        if (methodName == method.Name)
        {
            var ps = method.GetParameters();
            for (var i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.Name);
                if (!string.IsNullOrEmpty(ps[i].Name))
                {
                    sb.Append(' ');
                    sb.Append(ps[i].Name);
                }
            }
        }
        sb.Append(')');

        var file = frame.GetFileName();
        var line = frame.GetFileLineNumber();
        if (!string.IsNullOrEmpty(file))
        {
            sb.Append(" in ");
            sb.Append(Path.GetFileName(file));
            if (line > 0)
            {
                sb.Append(":line ");
                sb.Append(line);
            }
        }
        else if (line > 0)
        {
            sb.Append(":line ");
            sb.Append(line);
        }

        return sb.ToString();
    }
}
