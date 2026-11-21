using System.Globalization;
using PerformanceTool.Services;

namespace PerformanceTool.Client.Components;

/// <summary>Small formatting helpers.</summary>
public static class Format
{
    /// <summary>Elapsed time as m:ss, or h:mm:ss from one hour on.</summary>
    public static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string Num(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
    /// <summary>The burst shape of an entry in small text, e.g. "10 × 4/min"; empty for a linear entry (brain 0034).</summary>
    public static string Shape(ScenarioRunConfig c) => c.Pattern == SpawningPattern.Bursts ? $"{c.ActorsPerBurst} × {Rate(c.BurstsPerMinute)}/min" : "";

    public static string Rate(double value) => value.ToString(value >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture);

    /// <summary>A call or actor duration: "850 ms", "3.4 s", "1:05".</summary>
    public static string Ms(double ms) =>
        ms < 1000 ? $"{ms:0} ms" : ms < 60_000 ? $"{ms / 1000:0.0} s" : Time(ms / 1000);

    /// <summary>A ratio to the typical time (brain 0041): "1.4×". Only worth showing above 1.</summary>
    public static string Times(double ratio) => ratio.ToString("0.0", CultureInfo.InvariantCulture) + "×";

    /// <summary>Whether an actor is far enough over its typical time to name the figure ("1.4× typical"): below about 1.05 it only has the faintest tint.</summary>
    public static bool OverTypical(double ratio) => ratio >= 1.05;

    public static string Plural(int n, string one, string many) => $"{Num(n)} {(n == 1 ? one : many)}";
}
