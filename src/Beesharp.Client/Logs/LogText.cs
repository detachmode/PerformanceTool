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

    /// <summary>
    /// A log line for the clipboard, the same in every copy button of the app (user): both times, time into the run and clock time with
    /// milliseconds, then the level, the scenario and the bee when known, the message and the exception below it:
    /// <c>[0:05.9 · 14:02:07.312] ERR Checkout B7 GET /products/{id} · 3 → 503</c>. A source without a clock time shows the run time only.
    /// </summary>
    public static string CopyLine(double atSeconds, DateTimeOffset? at, string level, string message, string? exception,
        string scenario = "", int? sequence = null, TimeZoneInfo? zone = null)
    {
        var time = at is { } ts ? $"{Time(atSeconds)} · {TimeZoneInfo.ConvertTime(ts, zone ?? TimeZoneInfo.Local):HH:mm:ss.fff}" : Time(atSeconds);
        var head = $"[{time}] {Tag(level)} {(scenario.Length > 0 ? scenario + " " : "")}{(sequence is { } q && q >= 0 ? $"B{q + 1} " : "")}{message}";
        return string.IsNullOrEmpty(exception) ? head : head + "\n" + exception;
    }

    /// <summary>A line of the Logs page for the clipboard: both times (see <see cref="CopyLine"/>).</summary>
    public static string CopyLine(FakeLogEntry l, string scenario, TimeZoneInfo? zone = null) =>
        CopyLine(l.AtSeconds, l.Timestamp, l.Level, l.Message, l.Exception, scenario, l.Sequence, zone);

    /// <summary>The line as plain text (several lines when it has an exception).</summary>
    public static string Line(FakeLogEntry l, string scenario, LogTimeMode mode = LogTimeMode.Run, TimeZoneInfo? zone = null)
    {
        var head = $"[{Stamp(l, mode, zone)}] {Tag(l.Level)} {(scenario.Length > 0 ? scenario + " " : "")}B{l.Sequence + 1} {l.Message}";
        return string.IsNullOrEmpty(l.Exception) ? head : head + "\n" + l.Exception;
    }

    /// <summary>Lines joined for the clipboard, each with both times.</summary>
    public static string All(IEnumerable<FakeLogEntry> lines, Func<int, string> scenarioOf)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(CopyLine(l, scenarioOf(l.Entry)));
        }
        return sb.ToString();
    }
}
