using System.Globalization;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Logs;

/// <summary>The bee side panel of the Logs page (brain 0069): which bee is open (by sequence, B7 is 6) and its tab. Not part of the <see cref="LogFilter"/>: opening a bee leaves the list as it is.</summary>
public readonly record struct LogPanel(int? Bee = null, BeeTab Tab = BeeTab.Steps)
{
    public static readonly LogPanel None = new();
    public bool IsOpen => Bee is not null;
}

/// <summary>
/// The address of the Logs page with its bee side panel: <c>logs?level=error&amp;open=4158&amp;beeTab=calls</c>. <c>open</c> is the bee as the UI numbers it (B4158, from 1),
/// its own parameter and never the <c>bee=</c> filter; <c>beeTab</c> is the same parameter the scenario page uses (<c>logs|calls</c>, Steps is not written).
/// </summary>
public static class LogPanelLink
{
    /// <summary>The address (relative to the base href) of the Logs page with this filter and panel.</summary>
    public static string Href(LogFilter filter, LogPanel panel)
    {
        var parts = new List<string>();
        var q = filter.ToQueryString();
        if (q.Length > 0) parts.Add(q);
        if (panel.Bee is { } b)
        {
            parts.Add("open=" + (b + 1).ToString(CultureInfo.InvariantCulture));
            if (ScenarioLink.BeeTabName(panel.Tab) is { } tab) parts.Add("beeTab=" + tab);
        }
        return parts.Count == 0 ? "logs" : "logs?" + string.Join('&', parts);
    }

    /// <summary>The panel in the address parts: <c>open</c> (a bee number from 1) and <c>beeTab</c>. A missing or malformed <c>open</c> means closed.</summary>
    public static LogPanel Parse(string? open, string? beeTab) =>
        int.TryParse(open, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1
            ? new LogPanel(n - 1, ScenarioLink.ParseBeeTabName(beeTab))
            : LogPanel.None;
}
