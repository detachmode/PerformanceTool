using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;
using Beesharp.Services.Environments;

namespace Beesharp.Services;

/// <summary>
/// The one place that turns script text into a compiled Roslyn script (brain 0042, 0054) for scenario scripts, preflights and shutdown programs:
/// the common references and imports, the debug information that gives stack traces their file name and line numbers, the
/// <see cref="LegacyScriptNamespaces"/> upgrade and the mapping of diagnostics to the wire type. A kind only adds its own references and imports.
/// </summary>
internal static class ScriptCompiler
{
    /// <summary>The script options every kind starts from, plus the kind's own references and imports.</summary>
    public static ScriptOptions CreateOptions(IEnumerable<System.Reflection.Assembly> extraReferences, IEnumerable<string> extraImports)
    {
        var references = new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(HttpClient).Assembly,
            typeof(AuthenticationHeaderValue).Assembly,
            typeof(HttpClientJsonExtensions).Assembly,
            typeof(JsonSerializer).Assembly,
            typeof(ILogger).Assembly,
            typeof(EnvironmentValues).Assembly,
        }.Concat(extraReferences).Distinct();

        var imports = new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.Net.Http",
            "System.Net.Http.Headers",
            "System.Net.Http.Json",
            "System.Text",
            "System.Text.Json",
            "System.Text.Json.Nodes",
            "System.Threading",
            "System.Threading.Tasks",
            "Microsoft.Extensions.Logging",
            "Beesharp.Services.Environments",
        }.Concat(extraImports).Distinct();

        return ScriptOptions.Default.AddReferences(references).AddImports(imports);
    }

    /// <summary>
    /// Compiles <paramref name="code"/>. <c>Script</c> is null when there are errors; <c>Diagnostics</c> holds the errors and warnings either way.
    /// <paramref name="debugFile"/> is the file name stack traces show.
    /// </summary>
    public static (Script<object>? Script, List<ScriptDiagnosticDto> Diagnostics) Compile(
        string code, string debugFile, ScriptOptions options, Type globalsType, CancellationToken ct)
    {
        var withDebug = options
            .WithEmitDebugInformation(true)
            .WithFilePath(debugFile)
            .WithFileEncoding(System.Text.Encoding.UTF8);
        var script = CSharpScript.Create(LegacyScriptNamespaces.Upgrade(code), withDebug, globalsType);
        var mapped = script.Compile(ct)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(ToDiagnosticDto)
            .ToList();
        return (mapped.Any(d => d.Severity == "Error") ? null : script, mapped);
    }

    public static ScriptDiagnosticDto ToDiagnosticDto(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        return new ScriptDiagnosticDto(
            d.Id,
            d.Severity.ToString(),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            d.GetMessage());
    }
}

/// <summary>Script ids and file names, the same rules for every kind of script.</summary>
internal static class ScriptNames
{
    /// <summary>Letters, digits, <c>-</c> and <c>_</c> only; an empty result becomes a random id.</summary>
    public static string SanitizeId(string raw)
    {
        var clean = new string(raw.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return clean.Length == 0 ? $"script-{Guid.NewGuid():N}"[..18] : clean;
    }

    /// <summary>
    /// The file name to show and to compile as: the given one (without any path), else <c>{id}.cs</c>, else <paramref name="defaultName"/>.
    /// A name without <c>.cs</c> (or <c>.csx</c> when <paramref name="allowCsx"/>) gets <c>.cs</c> appended.
    /// </summary>
    public static string FileName(string? fileName, string? scriptId, string defaultName, bool allowCsx)
    {
        var name = string.IsNullOrWhiteSpace(fileName)
            ? (string.IsNullOrWhiteSpace(scriptId) ? defaultName : $"{scriptId}.cs")
            : Path.GetFileName(fileName.Trim());
        var ok = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || (allowCsx && name.EndsWith(".csx", StringComparison.OrdinalIgnoreCase));
        return ok ? name : name + ".cs";
    }
}

/// <summary>The files of one kind of script in one folder: <c>{id}.cs</c> (scenario scripts also read the older <c>{id}.csx</c>).</summary>
internal sealed class ScriptFolder(string directory, bool includeLegacyCsx)
{
    public sealed record ScriptFile(string Id, string FileName, string Code);

    /// <summary>Every script file in the folder (empty when the folder does not exist), in a stable order.</summary>
    public async Task<IReadOnlyList<ScriptFile>> ReadAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return [];
        var files = includeLegacyCsx
            ? Directory.EnumerateFiles(directory, "*.csx").Concat(Directory.EnumerateFiles(directory, "*.cs"))
            : Directory.EnumerateFiles(directory, "*.cs");
        var result = new List<ScriptFile>();
        foreach (var file in files.OrderBy(f => f))
            result.Add(new ScriptFile(Path.GetFileNameWithoutExtension(file), Path.GetFileName(file), await File.ReadAllTextAsync(file, ct)));
        return result;
    }

    /// <summary>Writes <c>{id}.cs</c> and removes an older <c>{id}.csx</c> of the same script.</summary>
    public async Task SaveAsync(string id, string code, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{id}.cs"), code, ct);
        if (includeLegacyCsx)
        {
            var legacy = Path.Combine(directory, $"{id}.csx");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
    }

    public void Delete(string id)
    {
        foreach (var ext in includeLegacyCsx ? new[] { ".cs", ".csx" } : [".cs"])
        {
            var path = Path.Combine(directory, id + ext);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
