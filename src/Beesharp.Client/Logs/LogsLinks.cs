using Microsoft.AspNetCore.Components;
using Beesharp.Client.Chart;

namespace Beesharp.Client.Logs;

/// <summary>
/// How the rest of the app gets to the Logs page (brain 0057): the figures and buttons that used to open the logs popup navigate to <c>/logs</c> with the filter
/// in the address instead. Also remembers the filter of the last visit, so the page is as you left it when you come back from another page.
/// </summary>
public sealed class LogsLinks(NavigationManager nav)
{
    public LogFilter Last { get; set; } = LogFilter.None;

    /// <summary>Opens the Logs page filtered to a scenario entry (0 based), a bee (sequence) and/or the logs of the failed or slow bees.</summary>
    public void Show(int? entry = null, int? bee = null, BeeShow outcome = BeeShow.All) =>
        Show(LogFilter.None.WithEntry(entry).WithBee(bee).WithOutcome(outcome));

    public void Show(LogFilter filter)
    {
        Last = filter;
        nav.NavigateTo(filter.Href());
    }
}
