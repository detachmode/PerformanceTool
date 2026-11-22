using System.Globalization;
using System.Text.RegularExpressions;

namespace Beesharp.Services.Scenarios.Spec;

/// <summary>Human-friendly durations for the editor: "500ms", "2s", "1.5s", "1m 30s". Plain numbers are milliseconds.</summary>
public static partial class DurationFormat
{
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ms|s|m|h)", RegexOptions.IgnoreCase)]
    private static partial Regex Part();

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            value = TimeSpan.FromMilliseconds(ms);
            return true;
        }

        var matches = Part().Matches(text);
        var compact = text.Replace(" ", "");
        if (matches.Count > 0 && string.Concat(matches.Select(m => m.Value.Replace(" ", ""))).Equals(compact, StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in matches)
            {
                var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                value += m.Groups[2].Value.ToLowerInvariant() switch
                {
                    "ms" => TimeSpan.FromMilliseconds(n),
                    "s" => TimeSpan.FromSeconds(n),
                    "m" => TimeSpan.FromMinutes(n),
                    _ => TimeSpan.FromHours(n),
                };
            }
            return true;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value);
    }

    public static string Format(TimeSpan t)
    {
        if (t == TimeSpan.Zero) return "0s";
        if (t.TotalSeconds < 1) return string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:0.###}ms");
        if (t.TotalMinutes < 1) return string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.###}s");
        var minutes = (int)t.TotalMinutes;
        var rest = t - TimeSpan.FromMinutes(minutes);
        return rest == TimeSpan.Zero ? $"{minutes}m" : $"{minutes}m {Format(rest)}";
    }
}
