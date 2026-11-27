using System.Globalization;

namespace Beesharp.Client.Runs;

/// <summary>
/// What the scenario page shows when it opens (brain 0059): the list filter, a step filter, and a bee. The numbers are the ones the user reads
/// in the UI (step 1 is the first step, bee 7 is B7), not zero-based indexes. <see cref="Default"/> is "all bees, nothing picked".
/// </summary>
public readonly record struct ScenarioFilter(DetailFilter Show = DetailFilter.All, int? FailedStep = null, int? NowStep = null, int? Bee = null)
{
    public static readonly ScenarioFilter Default = new();
}

/// <summary>The phone's view of the scenario page (brain 0059): the tab (<c>?tab=chart|bees</c>, Steps is the default and not written) and whether the bee sheet is open (<c>?sheet=1</c>).</summary>
public readonly record struct PhoneView(PhoneTab Tab = PhoneTab.Steps, bool Sheet = false)
{
    public static readonly PhoneView Default = new();
}

/// <summary>
/// The address of the scenario page and its query string (brain 0059): <c>scenario/2?show=failed</c>, <c>?failedStep=3</c>, <c>?nowStep=2</c>, <c>?bee=7</c>.
/// Scenario entries count from 1 like everywhere in the UI. One place maps filter to address and back, so the overview cards, the page and deep links agree.
/// </summary>
public static class ScenarioLink
{
    /// <summary>The relative address (to the base href) of scenario entry <paramref name="index"/> (zero-based) with a filter.</summary>
    public static string Href(int index, ScenarioFilter filter = default, BeeTab beeTab = BeeTab.Steps)
    {
        var parts = new List<string>(2);
        var show = ShowName(filter.Show);
        if (show is not null) parts.Add("show=" + show);
        if (filter.FailedStep is { } f) parts.Add("failedStep=" + (f + 1).ToString(CultureInfo.InvariantCulture));
        else if (filter.NowStep is { } n) parts.Add("nowStep=" + (n + 1).ToString(CultureInfo.InvariantCulture));
        if (filter.Bee is { } b) parts.Add("bee=" + (b + 1).ToString(CultureInfo.InvariantCulture));
        if (BeeTabName(beeTab) is { } tabName) parts.Add("beeTab=" + tabName);
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
                if (key is not ("show" or "failedstep" or "nowstep" or "bee")) kept.Add(pair);   // the phone's tab and sheet stay
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

    /// <summary>The same address with the phone's view replaced (<paramref name="view"/>); the filter and every other query part stay.</summary>
    public static string WithView(string uri, PhoneView view)
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
                if (key is not ("tab" or "sheet")) kept.Add(pair);
            }
        }
        if (view.Tab != PhoneTab.Steps) kept.Add("tab=" + view.Tab.ToString().ToLowerInvariant());
        if (view.Sheet) kept.Add("sheet=1");
        var path = "scenario/" + (index + 1).ToString(CultureInfo.InvariantCulture);
        return kept.Count == 0 ? path : path + "?" + string.Join("&", kept);
    }

    /// <summary>The same address with the bee column's tab replaced (<c>beeTab=logs|calls</c>, Steps is the default and not written); the filter, the phone's view and every other query part stay.</summary>
    public static string WithBeeTab(string uri, BeeTab tab)
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
                if (key != "beetab") kept.Add(pair);
            }
        }
        if (BeeTabName(tab) is { } name) kept.Add("beeTab=" + name);
        var path = "scenario/" + (index + 1).ToString(CultureInfo.InvariantCulture);
        return kept.Count == 0 ? path : path + "?" + string.Join("&", kept);
    }

    /// <summary>The bee column's tab in a query string or address (<c>beeTab=logs|calls</c>); anything else is Steps.</summary>
    public static BeeTab ParseBeeTab(string? query)
    {
        if (string.IsNullOrEmpty(query)) return BeeTab.Steps;
        var q = query;
        var mark = q.IndexOf('?');
        if (mark >= 0) q = q[(mark + 1)..];
        var hash = q.IndexOf('#');
        if (hash >= 0) q = q[..hash];
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            if (!key.Equals("beeTab", StringComparison.OrdinalIgnoreCase)) continue;
            return ParseBeeTabName(eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]));
        }
        return BeeTab.Steps;
    }

    /// <summary>"logs" or "calls" for the address; null for Steps (the default is not written).</summary>
    public static string? BeeTabName(BeeTab tab) => tab switch { BeeTab.Logs => "logs", BeeTab.Calls => "calls", _ => null };

    /// <summary>The tab a <c>beeTab</c> value names; unknown values mean Steps.</summary>
    public static BeeTab ParseBeeTabName(string? value) => value?.ToLowerInvariant() switch { "logs" => BeeTab.Logs, "calls" => BeeTab.Calls, _ => BeeTab.Steps };

    /// <summary>The phone's view encoded in a query string or address. Unknown values mean the default (Steps, no sheet).</summary>
    public static PhoneView ParseView(string? query)
    {
        if (string.IsNullOrEmpty(query)) return PhoneView.Default;
        var q = query;
        var mark = q.IndexOf('?');
        q = mark >= 0 ? q[(mark + 1)..] : "";
        var hash = q.IndexOf('#');
        if (hash >= 0) q = q[..hash];
        var tab = PhoneTab.Steps;
        var sheet = false;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]).ToLowerInvariant();
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]).ToLowerInvariant();
            if (key == "tab") tab = value switch { "chart" => PhoneTab.Chart, "bees" => PhoneTab.Bees, _ => PhoneTab.Steps };
            else if (key == "sheet") sheet = value is "1" or "true";
        }
        return new PhoneView(tab, sheet);
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
