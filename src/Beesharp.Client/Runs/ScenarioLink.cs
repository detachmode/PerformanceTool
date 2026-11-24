using System.Globalization;

namespace Beesharp.Client.Runs;

/// <summary>
/// What the scenario page shows when it opens (brain 0057): the list filter, a step filter, and a bee. The numbers are the ones the user reads
/// in the UI (step 1 is the first step, bee 7 is B7), not zero-based indexes. <see cref="Default"/> is "all bees, nothing picked".
/// </summary>
public readonly record struct ScenarioFilter(DetailFilter Show = DetailFilter.All, int? FailedStep = null, int? NowStep = null, int? Bee = null)
{
    public static readonly ScenarioFilter Default = new();
}

/// <summary>
/// The address of the scenario page and its query string (brain 0057): <c>scenario/2?show=failed</c>, <c>?failedStep=3</c>, <c>?nowStep=2</c>, <c>?bee=7</c>.
/// Scenario entries count from 1 like everywhere in the UI. One place maps filter to address and back, so the overview cards, the page and deep links agree.
/// </summary>
public static class ScenarioLink
{
    /// <summary>The relative address (to the base href) of scenario entry <paramref name="index"/> (zero-based) with a filter.</summary>
    public static string Href(int index, ScenarioFilter filter = default)
    {
        var parts = new List<string>(2);
        var show = ShowName(filter.Show);
        if (show is not null) parts.Add("show=" + show);
        if (filter.FailedStep is { } f) parts.Add("failedStep=" + (f + 1).ToString(CultureInfo.InvariantCulture));
        else if (filter.NowStep is { } n) parts.Add("nowStep=" + (n + 1).ToString(CultureInfo.InvariantCulture));
        if (filter.Bee is { } b) parts.Add("bee=" + (b + 1).ToString(CultureInfo.InvariantCulture));
        var path = "scenario/" + (index + 1).ToString(CultureInfo.InvariantCulture);
        return parts.Count == 0 ? path : path + "?" + string.Join("&", parts);
    }

    /// <summary>
    /// The same address with its filter parts replaced by <paramref name="filter"/>; every other query part (like the debug "?stepcount=") stays, so a reload shows the same view.
    /// <paramref name="uri"/> may be absolute or relative; the result is relative to the base address (<c>scenario/2?show=failed</c>).
    /// </summary>
    public static string WithFilter(string uri, ScenarioFilter filter)
    {
        var index = IndexOf(uri) ?? 0;
        var q = uri.IndexOf('?');
        var kept = new List<string>();
        if (q >= 0)
        {
            var rest = uri[(q + 1)..];
            var hash = rest.IndexOf('#');
            if (hash >= 0) rest = rest[..hash];
            foreach (var pair in rest.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]).ToLowerInvariant();
                if (key is not ("show" or "failedstep" or "nowstep" or "bee")) kept.Add(pair);
            }
        }
        var href = Href(index, filter);
        if (kept.Count == 0) return href;
        return href + (href.Contains('?') ? "&" : "?") + string.Join("&", kept);
    }

    /// <summary>The filter encoded in a query string (with or without the leading "?", or a whole address). Unknown or malformed parts are ignored.</summary>
    public static ScenarioFilter Parse(string? query)
    {
        if (string.IsNullOrEmpty(query)) return ScenarioFilter.Default;
        var q = query;
        var mark = q.IndexOf('?');
        if (mark >= 0) q = q[(mark + 1)..];
        var hash = q.IndexOf('#');
        if (hash >= 0) q = q[..hash];

        var show = DetailFilter.All;
        int? failed = null, now = null, bee = null;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            switch (key.ToLowerInvariant())
            {
                case "show": show = ParseShow(value); break;
                case "failedstep": failed = Number(value); break;
                case "nowstep": now = Number(value); break;
                case "bee": bee = Number(value); break;
            }
        }
        if (failed is not null) now = null;   // one step filter at a time
        return new ScenarioFilter(show, failed, now, bee);
    }

    /// <summary>The scenario number of a path like <c>/scenario/2</c> (zero-based result), or null when it is not a scenario address.</summary>
    public static int? IndexOf(string? path)
    {
        if (path is null) return null;
        var q = path.IndexOfAny(['?', '#']);
        if (q >= 0) path = path[..q];
        var at = path.LastIndexOf("scenario/", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = path[(at + "scenario/".Length)..].TrimEnd('/');
        return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n - 1 : null;
    }

    private static string? ShowName(DetailFilter f) => f switch
    {
        DetailFilter.Failed => "failed",
        DetailFilter.Slow => "slow",
        DetailFilter.Slowest5Pct => "slowest",
        _ => null,
    };

    private static DetailFilter ParseShow(string v) => v.ToLowerInvariant() switch
    {
        "failed" => DetailFilter.Failed,
        "slow" => DetailFilter.Slow,
        "slowest" => DetailFilter.Slowest5Pct,
        _ => DetailFilter.All,
    };

    /// <summary>A positive number from the address as a zero-based index, or null.</summary>
    private static int? Number(string v) => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n - 1 : null;
}
