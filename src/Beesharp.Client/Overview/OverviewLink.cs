using System.Globalization;

namespace Beesharp.Client.Overview;

/// <summary>The Overview's edit state (brain 0068): whether the plan is being edited and which scenario's settings panel is open (a zero-based entry; null = none).</summary>
public readonly record struct OverviewView(bool Edit = false, int? Entry = null)
{
    public static readonly OverviewView Viewing = new();
}

/// <summary>
/// Edit mode in the address, <c>/?edit=1&amp;entry=3</c> (entries count from 1 like everywhere in the UI), so a drill down to another page and Back (or a link, or a reload) lands in the same edit
/// state instead of silently leaving it. Edit mode is a state of the page, not a place to go back to, so changes replace the history entry. The older <c>?edit=&lt;anything&gt;</c> links
/// (the assistant's) still open the editor.
/// </summary>
public static class OverviewLink
{
    /// <summary>The address (relative to the base href): empty while viewing, <c>?edit=1</c> or <c>?edit=1&amp;entry=3</c> while editing.</summary>
    public static string Href(OverviewView view)
    {
        if (!view.Edit) return "";
        return view.Entry is { } e && e >= 0 ? "?edit=1&entry=" + (e + 1).ToString(CultureInfo.InvariantCulture) : "?edit=1";
    }

    /// <summary>
    /// What an address says about edit mode: null when it says nothing (no <c>edit</c> part), <see cref="OverviewView.Viewing"/> for <c>edit=0</c> or <c>edit=false</c>,
    /// otherwise editing (with the entry of <c>entry=n</c> when it is a valid number).
    /// </summary>
    public static OverviewView? Parse(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var q = uri.IndexOf('?');
        if (q < 0) return null;
        var rest = uri[(q + 1)..];
        var hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];
        string? edit = null;
        int? entry = null;
        foreach (var pair in rest.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]).ToLowerInvariant();
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (key == "edit") edit = value;
            else if (key == "entry" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1) entry = n - 1;
        }
        if (edit is null) return null;
        if (edit is "0" or "false") return OverviewView.Viewing;
        return new OverviewView(true, entry);
    }
}

/// <summary>Where the Overview was left (brain 0068): going to another page and back by the Overview tab (an address without <c>?edit</c>) restores edit mode and the open settings panel.</summary>
public sealed class OverviewEditMemory
{
    public OverviewView Last { get; set; } = OverviewView.Viewing;
}
