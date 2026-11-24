namespace Beesharp.Client.Runs;

/// <summary>Which bee comes before or after the selected one in the bee list of the scenario page (brain 0059, the ‹ › buttons of the bee column).</summary>
public static class BeeNavigator
{
    /// <summary>
    /// The sequence of the neighbour of <paramref name="current"/> in <paramref name="sequences"/> (the list as shown, top to bottom): <paramref name="direction"/> -1 is the one above, +1 the one below.
    /// Null at either end of the list (the button is disabled there) and for an empty list. A bee that is not in the list (picked on the chart, then the filter changed)
    /// goes to the first bee for "next" and to the last for "previous".
    /// </summary>
    public static int? Neighbour(IReadOnlyList<int> sequences, int? current, int direction)
    {
        if (sequences.Count == 0 || direction == 0) return null;
        var at = -1;
        if (current is { } c)
            for (var i = 0; i < sequences.Count; i++)
                if (sequences[i] == c) { at = i; break; }
        if (at < 0) return direction > 0 ? sequences[0] : sequences[^1];
        var next = at + (direction > 0 ? 1 : -1);
        return next >= 0 && next < sequences.Count ? sequences[next] : null;
    }
}
