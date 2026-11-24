using Beesharp.Client.Runs;

using System.Text;

namespace Beesharp.Client.Logs;

/// <summary>How a log line reads, on screen and in the clipboard: <c>[0:05.9] ERR Checkout B7 GET /products/{id} · 3 → 503</c>, an exception below it.</summary>
public static class LogText
{
    /// <summary>Time into the run with a tenth of a second: 0:05.9, 1:05.0, 1:02:03.4.</summary>
    public static string Time(double seconds)
    {
        var tenths = (long)Math.Floor(Math.Max(0, seconds) * 10);
        var t = TimeSpan.FromSeconds(tenths / 10);
        var frac = tenths % 10;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{frac}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{frac}";
    }

    public static string Tag(string level) => level switch
    {
        "Trace" => "TRC", "Debug" => "DBG", "Warning" => "WRN", "Error" => "ERR", "Critical" => "FTL", _ => "INF",
    };

    /// <summary>The line as plain text (several lines when it has an exception).</summary>
    public static string Line(FakeLogEntry l, string scenario)
    {
        var bee = l.Sequence >= 0 ? $"B{l.Sequence + 1} " : "";
        var head = $"[{Time(l.AtSeconds)}] {Tag(l.Level)} {(scenario.Length > 0 ? scenario + " " : "")}{bee}{l.Message}";
        return string.IsNullOrEmpty(l.Exception) ? head : head + "\n" + l.Exception;
    }

    /// <summary>Lines joined for the clipboard.</summary>
    public static string All(IEnumerable<FakeLogEntry> lines, Func<int, string> scenarioOf)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Line(l, scenarioOf(l.Entry)));
        }
        return sb.ToString();
    }
}
