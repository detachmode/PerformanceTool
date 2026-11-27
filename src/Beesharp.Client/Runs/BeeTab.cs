namespace Beesharp.Client.Runs;

/// <summary>The tabs of the bee view (brain 0059, 0068): its steps, its logs, its calls. Kept in the address as <c>beeTab=logs|calls</c> (Steps is the default and not written).</summary>
public enum BeeTab { Steps, Logs, Calls }

/// <summary>Where a bee is in a run: which scenario entry it belongs to and whether it has finished (<see cref="Finished"/>) or is still in flight (<see cref="Running"/>).</summary>
public readonly record struct BeeLocation(int Entry, BeeRecord? Finished, RunningBee? Running);

/// <summary>Finds a bee by its sequence number (B7 is sequence 6) in a run snapshot, for the places that only know the number: the Logs page's bee side panel, a link from the address (brain 0068).</summary>
public static class BeeLookup
{
    /// <summary>The bee with this sequence, finished or in flight, or null when the run does not hold it (another run, or a number that was never started).</summary>
    public static BeeLocation? Find(RunSnapshot s, int sequence)
    {
        if (sequence < 0) return null;
        if (s.BeesInFlight is { } flying)
            for (var e = 0; e < flying.Length; e++)
                foreach (var r in flying[e])
                    if (r.Sequence == sequence) return new BeeLocation(e, null, r);
        for (var e = 0; e < s.Count && e < s.Scenarios.Length; e++)
        {
            var all = s.Store.Bees(e);
            var n = Math.Min(s.Scenarios[e].Finished, all.Count);
            for (var i = 0; i < n; i++)
                if (all[i].Sequence == sequence) return new BeeLocation(e, all[i], null);
        }
        return null;
    }
}
