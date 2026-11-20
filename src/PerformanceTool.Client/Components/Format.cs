using System.Globalization;

namespace PerformanceTool.Client.Components;

/// <summary>DRAFT: small formatting helpers for the draft pages.</summary>
public static class Format
{
    public static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string Num(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
    public static string Rate(double value) => value.ToString(value >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture);
}
