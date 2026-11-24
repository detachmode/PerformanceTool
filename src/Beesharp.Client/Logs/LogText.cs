using Beesharp.Client.Runs;

using System.Text;

namespace Beesharp.Client.Logs;

/// <summary>How the Logs page shows a line's time: time into the run (0:05.9) or the clock time it was written (14:02:07.3, local time).</summary>
public enum LogTimeMode { Run, Clock }

/// <summary>How a log line reads, on screen and in the clipboard: <c>[0:05.9] ERR Checkout B7 GET /products/{id} · 3 → 503</c>, an exception below it.</summary>
public static class LogText
{
    /// <summary>Clock time with a tenth of a second, in <paramref name="zone"/> (default: the browser's time zone): 14:02:07.3.</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local);
        return $"{local:HH:mm:ss}.{local.Millisecond / 100}";
    }

    /// <summary>The time of a line in the chosen mode.</summary>
    public static string Stamp(FakeLogEntry l, LogTimeMode mode, TimeZoneInfo? zone = null) =>
        mode == LogTimeMode.Clock ? Clock(l.Timestamp, zone) : Time(l.AtSeconds);

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
    public static string Line(FakeLogEntry l, string scenario, LogTimeMode mode = LogTimeMode.Run, TimeZoneInfo? zone = null)
    {
        var head = $"[{Stamp(l, mode, zone)}] {Tag(l.Level)} {(scenario.Length > 0 ? scenario + " " : "")}B{l.Sequence + 1} {l.Message}";
        return string.IsNullOrEmpty(l.Exception) ? head : head + "\n" + l.Exception;
    }

    /// <summary>Lines joined for the clipboard.</summary>
    public static string All(IEnumerable<FakeLogEntry> lines, Func<int, string> scenarioOf, LogTimeMode mode = LogTimeMode.Run)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Line(l, scenarioOf(l.Entry), mode));
        }
        return sb.ToString();
    }
}
