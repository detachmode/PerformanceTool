using System.Diagnostics;
using System.Reflection;
using System.Text;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Services;

/// <summary>
/// Formats an exception thrown by a scenario or preflight script so the stack trace only shows frames
/// from that scenario's own custom code (Roslyn script or scenario DLL), hiding Beesharp host/runner plumbing.
/// </summary>
public static class ScenarioExceptionFormatter
{
    private static readonly Assembly CoreAssembly = typeof(IScenario).Assembly;

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

        // 1. If the scenario lives in its own external DLL (not Beesharp.Core's DelegateScenario wrapper),
        //    match only frames from that DLL (excluding any Beesharp host types).
        var isDelegateWrapper = scenarioAssembly is null || scenarioAssembly == CoreAssembly;
        if (!isDelegateWrapper)
        {
            foreach (var f in all)
            {
                var m = f.GetMethod();
                if (m is null || IsHostOrPlumbing(m)) continue;
                if (m.DeclaringType?.Assembly == scenarioAssembly)
                    filtered.Add(FormatFrame(f, m));
            }
        }
        else
        {
            // Roslyn script: match frames from dynamically compiled script assemblies (Submission#* / ℛ* / custom script types)
            foreach (var f in all)
            {
                var m = f.GetMethod();
                if (m is null || IsHostOrPlumbing(m)) continue;
                var asm = m.DeclaringType?.Assembly;
                if (asm is null) continue;
                var asmName = asm.GetName().Name ?? "";
                var typeName = m.DeclaringType?.FullName ?? "";
                if (asm.IsDynamic
                    || asmName.StartsWith("ℛ", StringComparison.Ordinal)
                    || typeName.StartsWith("Submission#", StringComparison.Ordinal)
                    || (!asmName.StartsWith("System", StringComparison.Ordinal)
                        && !asmName.StartsWith("Microsoft", StringComparison.Ordinal)
                        && !asmName.StartsWith("Beesharp", StringComparison.Ordinal)
                        && !asmName.StartsWith("PerformanceTool", StringComparison.Ordinal)))
                {
                    filtered.Add(FormatFrame(f, m));
                }
            }
        }

        if (filtered.Count > 0)
            return filtered;

        // 2. Fallback: exclude Beesharp host/runner plumbing and async runtime internals.
        foreach (var f in all)
        {
            var m = f.GetMethod();
            if (m is null || IsHostOrPlumbing(m)) continue;
            filtered.Add(FormatFrame(f, m));
        }

        return filtered;
    }

    private static bool IsHostOrPlumbing(MethodBase method)
    {
        var ns = method.DeclaringType?.Namespace ?? "";
        var full = method.DeclaringType?.FullName ?? "";
        var asmName = method.DeclaringType?.Assembly.GetName().Name ?? "";

        if (full.StartsWith("PerformanceTool.Services.", StringComparison.Ordinal)
            || full.StartsWith("PerformanceTool.Client.", StringComparison.Ordinal)
            || asmName.Equals("Beesharp", StringComparison.Ordinal)
            || asmName.Equals("Beesharp.Core", StringComparison.Ordinal)
            || ns.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal)
            || ns.StartsWith("System.Runtime.ExceptionServices", StringComparison.Ordinal)
            || ns.StartsWith("System.Threading.Tasks", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static string FormatFrame(StackFrame frame, MethodBase method)
    {
        var type = method.DeclaringType;
        var typeName = type?.FullName ?? "";
        var methodName = method.Name;
        var isCompilerGeneratedLambda = false;

        // Clean up compiler-generated async state machine names like:
        //   "MyScenario+<ExecuteAsync>d__4.MoveNext"
        //   "Submission#0+<>c+<<<Initialize>>b__0_0>d.MoveNext"
        if (methodName == "MoveNext" && type is not null && type.Name.StartsWith('<'))
        {
            var outer = type.DeclaringType;
            if (outer is not null)
            {
                typeName = outer.FullName ?? outer.Name;
                // Extract the inner method name between '<' and '>'
                var inner = type.Name;
                if (inner.Contains("<<Initialize>>", StringComparison.Ordinal) || inner.Contains(">b__", StringComparison.Ordinal))
                {
                    isCompilerGeneratedLambda = true;
                    methodName = "Execute";
                }
                else
                {
                    var close = inner.IndexOf('>');
                    if (close > 1) methodName = inner[1..close];
                }
            }
        }
        else if (methodName.StartsWith('<') && methodName.Contains(">b__", StringComparison.Ordinal))
        {
            isCompilerGeneratedLambda = true;
            methodName = "Execute";
        }

        // Clean up Roslyn Submission#0 and <>c closure display class names
        typeName = typeName
            .Replace("Submission#0+<>c", "Script", StringComparison.Ordinal)
            .Replace("Submission#0+", "", StringComparison.Ordinal);
        if (typeName is "Submission#0" or "<>c")
            typeName = "Script";
        else if (typeName.EndsWith("+<>c", StringComparison.Ordinal))
            typeName = typeName[..^4];

        var sb = new StringBuilder();
        if (typeName.Length > 0)
        {
            sb.Append(typeName);
            sb.Append('.');
        }
        sb.Append(methodName);
        sb.Append('(');
        if (!isCompilerGeneratedLambda && methodName == method.Name)
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
