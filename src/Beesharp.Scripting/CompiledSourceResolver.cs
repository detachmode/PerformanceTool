using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services;

/// <summary>
/// Resolves original C# source files for compiled <see cref="IScenario"/> and <see cref="IRunProgram"/> types
/// using Portable PDB debug information (embedded sources first, then local source paths on disk).
/// </summary>
public static class CompiledSourceResolver
{
    /// <summary>Origin value when source was extracted from Portable PDB embedded source records.</summary>
    public const string OriginEmbeddedPdb = "EmbeddedPdb";

    /// <summary>Origin value when source was read from the local file path recorded in the Portable PDB.</summary>
    public const string OriginDisk = "Disk";

    /// <summary>Origin value when neither embedded PDB source nor a local file was available.</summary>
    public const string OriginUnavailable = "Unavailable";

    /// <summary>Origin value when source comes from a mutable C# script file.</summary>
    public const string OriginScript = "Script";

    /// <summary>Computes the lowercase hex SHA-256 digest of UTF-8 encoded source code.</summary>
    /// <param name="code">Source code text.</param>
    /// <returns>64-character lowercase hex SHA-256 string.</returns>
    public static string ComputeSha256(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code ?? string.Empty)));

    /// <summary>
    /// Computes the current SHA-256 digest of a scenario, preflight, or shutdown program for drift comparison against a stored snapshot.
    /// Returns <see langword="null"/> when the target is no longer registered.
    /// </summary>
    /// <param name="kind"><c>scenario</c>, <c>preflight</c>, or <c>shutdown</c>.</param>
    /// <param name="targetId">Scenario ID or program script ID.</param>
    /// <param name="catalog">Scenario catalog for compiled scenarios.</param>
    /// <param name="scripts">Optional script scenario store.</param>
    /// <param name="preflights">Optional preflight program store.</param>
    /// <param name="shutdowns">Optional shutdown program store.</param>
    /// <returns>The current lowercase SHA-256 digest, or <see langword="null"/> if not found.</returns>
    public static string? ResolveCurrentSha256(
        string kind,
        string targetId,
        ScenarioCatalog? catalog,
        ScriptScenarioStore? scripts,
        PreflightStore? preflights,
        ShutdownStore? shutdowns)
    {
        if (string.Equals(kind, "preflight", StringComparison.OrdinalIgnoreCase))
            return preflights?.Get(targetId) is { } pf ? ComputeSha256(pf.Code) : null;

        if (string.Equals(kind, "shutdown", StringComparison.OrdinalIgnoreCase))
            return shutdowns?.Get(targetId) is { } sd ? ComputeSha256(sd.Code) : null;

        if (scripts is not null && scripts.FindScriptIdForScenario(targetId) is { } scriptId && scripts.Get(scriptId) is { } sc)
            return ComputeSha256(sc.Code);

        if (catalog?.Find(targetId) is { } compiled)
            return ComputeSha256(ResolveScenario(compiled).Code);

        return null;
    }

    /// <summary>
    /// Captures source code snapshots for all distinct scenarios (and optional preflight/shutdown programs) in <paramref name="plan"/>.
    /// </summary>
    /// <param name="plan">The load plan about to execute.</param>
    /// <param name="catalog">Scenario catalog for compiled scenarios.</param>
    /// <param name="scripts">Optional script scenario store.</param>
    /// <param name="preflights">Optional preflight program store.</param>
    /// <param name="shutdowns">Optional shutdown program store.</param>
    /// <returns>List of captured <see cref="RunSourceSnapshotDto"/> entries.</returns>
    public static List<RunSourceSnapshotDto> CaptureForPlan(
        LoadPlan plan,
        ScenarioCatalog? catalog,
        ScriptScenarioStore? scripts,
        PreflightStore? preflights,
        ShutdownStore? shutdowns)
    {
        var snapshots = new List<RunSourceSnapshotDto>();
        var seenScenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in plan.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(entry.ScenarioId) || !seenScenarios.Add(entry.ScenarioId))
                continue;

            if (scripts is not null && scripts.FindScriptIdForScenario(entry.ScenarioId) is { } scriptId && scripts.Get(scriptId) is { } sc)
            {
                snapshots.Add(new RunSourceSnapshotDto(
                    "scenario",
                    entry.ScenarioId,
                    sc.ScenarioName,
                    sc.FileName,
                    sc.Code,
                    ComputeSha256(sc.Code),
                    OriginScript,
                    IsCompiled: false));
            }
            else if (catalog?.Find(entry.ScenarioId) is { } compiled)
            {
                var resolved = ResolveScenario(compiled);
                snapshots.Add(new RunSourceSnapshotDto(
                    "scenario",
                    compiled.Id,
                    compiled.Name,
                    resolved.FileName,
                    resolved.Code,
                    ComputeSha256(resolved.Code),
                    resolved.Origin,
                    IsCompiled: true));
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.PreflightScriptId) && preflights?.Get(plan.PreflightScriptId) is { } pf)
        {
            snapshots.Add(new RunSourceSnapshotDto(
                "preflight",
                pf.ScriptId,
                pf.Name,
                pf.FileName,
                pf.Code,
                ComputeSha256(pf.Code),
                pf.IsCompiled ? OriginEmbeddedPdb : OriginScript,
                pf.IsCompiled));
        }

        if (!string.IsNullOrWhiteSpace(plan.ShutdownScriptId) && shutdowns?.Get(plan.ShutdownScriptId) is { } sd)
        {
            snapshots.Add(new RunSourceSnapshotDto(
                "shutdown",
                sd.ScriptId,
                sd.Name,
                sd.FileName,
                sd.Code,
                ComputeSha256(sd.Code),
                sd.IsCompiled ? OriginEmbeddedPdb : OriginScript,
                sd.IsCompiled));
        }

        return snapshots;
    }

    // Portable PDB specification GUID for EmbeddedSource CustomDebugInformation records.
    private static readonly Guid EmbeddedSourceGuid = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    // Portable PDB magic header ("BSJB").
    private static ReadOnlySpan<byte> PortablePdbMagic => "BSJB"u8;

    /// <summary>
    /// Resolves C# source for a compiled <see cref="IScenario"/>, including its parameter type file when separate.
    /// </summary>
    /// <param name="scenario">The compiled scenario instance.</param>
    /// <returns>A <see cref="CompiledScenarioSourceDto"/> containing the resolved source or a helpful fallback comment.</returns>
    public static CompiledScenarioSourceDto ResolveScenario(IScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var scenarioType = scenario.GetType();
        var paramType = scenario.Spec.Type;

        var types = paramType is not null && paramType != scenarioType && paramType != typeof(object)
            ? new[] { scenarioType, paramType }
            : new[] { scenarioType };

        var files = ResolveFiles(types);
        if (files.Count == 0)
        {
            var fallbackFile = $"{scenarioType.Name}.cs";
            var fallbackCode = BuildUnavailableComment(scenarioType);
            return new CompiledScenarioSourceDto(
                scenario.Id,
                scenario.Name,
                fallbackFile,
                fallbackCode,
                OriginUnavailable,
                scenarioType.FullName ?? scenarioType.Name);
        }

        var primary = files[0];
        var combinedCode = files.Count == 1
            ? primary.Code
            : string.Join("\n\n", files.Select(f => $"// ── {f.FileName} ──\n{f.Code}"));

        return new CompiledScenarioSourceDto(
            scenario.Id,
            scenario.Name,
            primary.FileName,
            combinedCode,
            primary.Origin,
            scenarioType.FullName ?? scenarioType.Name,
            files.Count > 1 ? files : null);
    }

    /// <summary>
    /// Resolves C# source for a single compiled CLR type (such as a compiled preflight or shutdown program).
    /// </summary>
    /// <param name="type">The compiled CLR type.</param>
    /// <returns>A tuple of <c>(FileName, Code, Origin)</c>.</returns>
    public static (string FileName, string Code, string Origin) ResolveType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var files = ResolveFiles([type]);
        if (files.Count == 0)
            return ($"{type.Name}.cs", BuildUnavailableComment(type), OriginUnavailable);

        var primary = files[0];
        var combinedCode = files.Count == 1
            ? primary.Code
            : string.Join("\n\n", files.Select(f => $"// ── {f.FileName} ──\n{f.Code}"));
        return (primary.FileName, combinedCode, primary.Origin);
    }

    private static List<CompiledSourceFileDto> ResolveFiles(IEnumerable<Type> types)
    {
        var result = new List<CompiledSourceFileDto>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in types.GroupBy(t => t.Assembly))
        {
            var assembly = group.Key;
            if (!TryOpenPdb(assembly, out var provider, out var streamToDispose))
                continue;

            using (streamToDispose)
            using (provider)
            {
                var reader = provider.GetMetadataReader();
                foreach (var type in group)
                {
                    foreach (var docHandle in FindDocumentsForType(type, reader))
                    {
                        var doc = reader.GetDocument(docHandle);
                        var rawPath = reader.GetString(doc.Name);
                        if (string.IsNullOrWhiteSpace(rawPath) || !seenPaths.Add(rawPath))
                            continue;

                        var fileName = ExtractFileName(rawPath);
                        if (TryReadEmbeddedSource(reader, docHandle, out var embeddedCode))
                        {
                            result.Add(new CompiledSourceFileDto(fileName, embeddedCode, OriginEmbeddedPdb));
                        }
                        else if (TryReadDiskSource(rawPath, out var diskCode))
                        {
                            result.Add(new CompiledSourceFileDto(fileName, diskCode, OriginDisk));
                        }
                    }
                }
            }
        }

        return result;
    }

    private static bool TryOpenPdb(
        Assembly assembly,
        out MetadataReaderProvider provider,
        out IDisposable? streamToDispose)
    {
        provider = null!;
        streamToDispose = null;

        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            return false;

        // 1. Side-by-side Portable PDB (<assembly>.pdb)
        var pdbPath = Path.ChangeExtension(location, ".pdb");
        if (File.Exists(pdbPath) && IsPortablePdb(pdbPath))
        {
            try
            {
                var fs = File.OpenRead(pdbPath);
                provider = MetadataReaderProvider.FromPortablePdbStream(fs);
                return true;
            }
            catch
            {
                // Fall through to embedded PDB check.
            }
        }

        // 2. Embedded Portable PDB inside the PE image (<DebugType>embedded</DebugType>)
        try
        {
            var peStream = File.OpenRead(location);
            var peReader = new PEReader(peStream);
            foreach (var entry in peReader.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    streamToDispose = peReader;
                    return true;
                }
            }
            peReader.Dispose();
        }
        catch
        {
            // Ignore unreadable PE / debug directory.
        }

        return false;
    }

    private static bool IsPortablePdb(string pdbPath)
    {
        try
        {
            Span<byte> header = stackalloc byte[4];
            using var fs = File.OpenRead(pdbPath);
            return fs.Read(header) == 4 && header.SequenceEqual(PortablePdbMagic);
        }
        catch
        {
            return false;
        }
    }

    private static List<DocumentHandle> FindDocumentsForType(Type type, MetadataReader reader)
    {
        var docs = new List<DocumentHandle>();
        var seen = new HashSet<DocumentHandle>();

        void AddDoc(DocumentHandle h)
        {
            if (!h.IsNil && seen.Add(h))
                docs.Add(h);
        }

        void InspectType(Type t, int depth)
        {
            const BindingFlags Flags = BindingFlags.DeclaredOnly
                                     | BindingFlags.Public
                                     | BindingFlags.NonPublic
                                     | BindingFlags.Instance
                                     | BindingFlags.Static;

            foreach (var method in t.GetConstructors(Flags).Cast<MethodBase>().Concat(t.GetMethods(Flags)))
            {
                int token;
                try { token = method.MetadataToken; }
                catch { continue; }

                // MethodDef table is 0x06.
                if ((token & 0xFF000000) != 0x06000000)
                    continue;

                var handle = MetadataTokens.MethodDefinitionHandle(token);
                MethodDebugInformation debugInfo;
                try { debugInfo = reader.GetMethodDebugInformation(handle); }
                catch { continue; }

                AddDoc(debugInfo.Document);
                if (!debugInfo.SequencePointsBlob.IsNil)
                {
                    foreach (var sp in debugInfo.GetSequencePoints())
                        AddDoc(sp.Document);
                }
            }

            // Compiler-generated async state machines and lambda display classes are nested types.
            if (depth < 2)
            {
                foreach (var nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    InspectType(nested, depth + 1);
            }
        }

        InspectType(type, 0);
        return docs;
    }

    private static bool TryReadEmbeddedSource(MetadataReader reader, DocumentHandle docHandle, out string code)
    {
        code = string.Empty;
        try
        {
            foreach (var cdiHandle in reader.GetCustomDebugInformation(docHandle))
            {
                var cdi = reader.GetCustomDebugInformation(cdiHandle);
                if (reader.GetGuid(cdi.Kind) != EmbeddedSourceGuid)
                    continue;

                var blob = reader.GetBlobReader(cdi.Value);
                if (blob.Length < 4)
                    return false;

                var format = blob.ReadInt32();
                byte[] bytes;
                if (format == 0)
                {
                    bytes = blob.ReadBytes(blob.RemainingBytes);
                }
                else if (format > 0)
                {
                    var compressed = blob.ReadBytes(blob.RemainingBytes);
                    using var input = new MemoryStream(compressed);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream(format);
                    deflate.CopyTo(output);
                    bytes = output.ToArray();
                }
                else
                {
                    return false;
                }

                code = DecodeUtf8(bytes);
                return true;
            }
        }
        catch
        {
            // Fall back to disk if embedded blob cannot be decoded.
        }

        return false;
    }

    private static bool TryReadDiskSource(string path, out string code)
    {
        code = string.Empty;
        try
        {
            if (!File.Exists(path))
                return false;
            code = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static string ExtractFileName(string path)
    {
        var idx = path.LastIndexOfAny(['/', '\\']);
        return idx >= 0 && idx + 1 < path.Length ? path[(idx + 1)..] : path;
    }

    private static string BuildUnavailableComment(Type type) =>
        $"// Compiled C# class: {type.FullName}\n// Source code is unavailable (no Portable PDB with embedded source or local .cs file was found).\n// Build the project with <EmbedAllSources>true</EmbedAllSources> to embed scenario sources in the PDB.";
}
