using System.Globalization;
using System.Text.RegularExpressions;

namespace Beesharp.Services.Scenarios.Spec;

/// <summary>
/// The non-linear scale of the run's duration slider (brain 0073). The track is a grid of nice values in four segments, so the thumb
/// moves one snap step per position (the arrow keys too): 5 s to 1 min in 5 s steps, 1 to 10 min in 30 s steps, 10 to 60 min in 5 min
/// steps and 1 to 6 h in 30 min steps. Seconds get about a fifth of the track, minutes most of it. The plan keeps plain seconds
/// (<c>LoadPlan.DurationSeconds</c>); this class only maps between them and slider positions, formats them as time and parses typed text.
/// </summary>
public static partial class DurationScale
{
    private static readonly (int From, int To, int Step)[] Segments =
    [
        (5, 60, 5),
        (60, 600, 30),
        (600, 3600, 300),
        (3600, 21600, 1800),
    ];

    private static readonly int[] Grid = BuildGrid();

    /// <summary>Slider positions run from 0 to this value; each position is one snap step.</summary>
    public static int MaxPosition => Grid.Length - 1;

    /// <summary>The shortest value on the track (typed values may be shorter; the thumb then sits at 0).</summary>
    public static int MinSeconds => Grid[0];

    /// <summary>The longest value on the track (typed values may be longer; the thumb then sits at the end).</summary>
    public static int MaxSeconds => Grid[^1];

    /// <summary>The faint marks under the track: the segment boundaries and the end, with their label.</summary>
    public static IReadOnlyList<DurationMark> Marks { get; } = BuildMarks();

    /// <summary>The short list the quick-pick menu offers, in seconds.</summary>
    public static IReadOnlyList<int> Presets { get; } = [30, 60, 300, 600, 1800, 3600];

    /// <summary>The value at a slider position; positions outside the track are clamped.</summary>
    public static int SecondsAt(int position) => Grid[Math.Clamp(position, 0, Grid.Length - 1)];

    /// <summary>The position nearest to <paramref name="seconds"/> (the lower one on a tie); the ends for values outside the track.</summary>
    public static int PositionFor(int seconds)
    {
        if (seconds <= Grid[0]) return 0;
        if (seconds >= Grid[^1]) return Grid.Length - 1;
        var i = Array.BinarySearch(Grid, seconds);
        if (i >= 0) return i;
        var upper = ~i;
        return seconds - Grid[upper - 1] <= Grid[upper] - seconds ? upper - 1 : upper;
    }

    /// <summary>The grid value nearest to <paramref name="seconds"/>.</summary>
    public static int Snap(int seconds) => Grid[PositionFor(seconds)];

    /// <summary>Whether <paramref name="seconds"/> is exactly a value of the grid (otherwise it was typed off the grid).</summary>
    public static bool IsOnGrid(int seconds) => Array.BinarySearch(Grid, seconds) >= 0;

    /// <summary>Formats seconds as time: <c>45 s</c>, <c>2 min 30 s</c>, <c>1 h 30 min</c>, <c>1 h</c>.</summary>
    public static string Format(int seconds)
    {
        if (seconds <= 0) return "0 s";
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add($"{h} h");
        if (m > 0) parts.Add($"{m} min");
        if (s > 0) parts.Add($"{s} s");
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Parses typed text into whole seconds (at least 1): <c>90</c> and <c>90s</c> (a plain number is seconds), <c>2m</c>, <c>2 min 30 s</c>,
    /// <c>1h 30m</c>, <c>1.5m</c>, <c>1:30</c> (minutes and seconds) and <c>1:30:00</c> (hours, minutes, seconds). Fractions round to the second.
    /// </summary>
    public static bool TryParse(string? text, out int seconds)
    {
        seconds = 0;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        double total;
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length is < 2 or > 3) return false;
            total = 0;
            foreach (var p in parts)
            {
                if (!double.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
                total = total * 60 + n;
            }
        }
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            total = plain;
        }
        else
        {
            var normalized = Words().Replace(text, m =>
            {
                var w = m.Value.ToLowerInvariant();
                return w.StartsWith("sec", StringComparison.Ordinal) ? "s" : w.StartsWith("min", StringComparison.Ordinal) ? "m" : "h";
            });
            if (!DurationFormat.TryParse(normalized, out var span)) return false;
            total = span.TotalSeconds;
        }

        if (double.IsNaN(total) || total < 0.5 || total > int.MaxValue) return false;
        seconds = (int)Math.Round(total, MidpointRounding.AwayFromZero);
        return true;
    }

    [GeneratedRegex(@"(?<=\d\s*)(seconds?|secs?|minutes?|mins?|hours?|hrs?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Words();

    private static int[] BuildGrid()
    {
        var values = new List<int>();
        foreach (var (from, to, step) in Segments)
            for (var v = from; v <= to; v += step)
                if (values.Count == 0 || v > values[^1]) values.Add(v);
        return [.. values];
    }

    private static IReadOnlyList<DurationMark> BuildMarks()
    {
        var marks = new List<DurationMark>();
        foreach (var seconds in new[] { 60, 600, 3600, 21600 })
            marks.Add(new DurationMark(Array.IndexOf(Grid, seconds), seconds, Format(seconds)));
        return marks;
    }
}

/// <summary>A faint mark under the duration slider: where on the track (position), which value and its label.</summary>
public readonly record struct DurationMark(int Position, int Seconds, string Label);
