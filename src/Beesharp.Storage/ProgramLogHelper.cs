using System.Text.RegularExpressions;

namespace Beesharp.Services.Storage;

/// <summary>Shared parsing and entry-index mapping for persisted preflight and shutdown program runs.</summary>
public static partial class ProgramLogHelper
{
    [GeneratedRegex(@"^\[(?<ts>[^\]]+?)(?:\s+(?<lvl>TRC|VRB|DBG|INF|WRN|ERR|FTL|OUT))?\]\s*(?:\[(?<lvl2>TRC|VRB|DBG|INF|WRN|ERR|FTL|OUT)\]\s*)?(?<msg>[\s\S]*)$")]
    private static partial Regex PrefixRegex();

    public static (string NormalizedKind, int Entry, string BeeName) ResolveKind(string kind)
    {
        if (string.Equals(kind, "preflight", StringComparison.OrdinalIgnoreCase))
            return ("preflight", ProgramLogEntry.Preflight, "Preflight");
        if (string.Equals(kind, "shutdown", StringComparison.OrdinalIgnoreCase))
            return ("shutdown", ProgramLogEntry.Shutdown, "Shutdown");
        throw new ArgumentException($"Unsupported program kind '{kind}'. Expected 'preflight' or 'shutdown'.", nameof(kind));
    }

    /// <summary>Parses a raw program log line (e.g. <c>[19:05:12.345] [INF] Ready</c>) into a normalized level name and message.</summary>
    public static (string Level, string Message) ParseLine(string raw)
    {
        var m = PrefixRegex().Match(raw);
        if (m.Success)
        {
            var tag = m.Groups["lvl"].Success ? m.Groups["lvl"].Value
                : m.Groups["lvl2"].Success ? m.Groups["lvl2"].Value
                : "INF";
            return (NormalizeTag(tag), m.Groups["msg"].Value);
        }

        var level = raw.Contains("[ERR]", StringComparison.Ordinal) || raw.Contains("[FTL]", StringComparison.Ordinal) ? "Error"
            : raw.Contains("[WRN]", StringComparison.Ordinal) ? "Warning"
            : raw.Contains("[DBG]", StringComparison.Ordinal) ? "Debug"
            : raw.Contains("[TRC]", StringComparison.Ordinal) ? "Trace"
            : "Information";
        return (level, raw);
    }

    private static string NormalizeTag(string tag) => tag switch
    {
        "TRC" or "VRB" => "Trace",
        "DBG" => "Debug",
        "WRN" => "Warning",
        "ERR" or "FTL" => "Error",
        _ => "Information",
    };
}
