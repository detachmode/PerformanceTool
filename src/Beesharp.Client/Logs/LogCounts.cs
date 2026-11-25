using Beesharp.Client.Runs;

namespace Beesharp.Client.Logs;

/// <summary>
/// The counts of the Logs page's filter column for the logs the browser holds (Fake mode): the same numbers the server's <c>logs/facets</c> gives
/// for stored runs (<see cref="LogFacets"/>). One pass over the lines; each group ignores its own filter, so the other choices show what they would give.
/// </summary>
public static class LogCounts
{
    public static LogFacets Count(IReadOnlyList<FakeLogEntry> lines, LogFilter filter, RunStore store)
    {
        long total = 0;
        long preflight = 0;
        long shutdown = 0;
        long host = 0;
        var levels = new Dictionary<string, long>();
        var entries = new List<long>();
        var steps = new SortedDictionary<int, (long Count, long Errors)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var all = filter.Matches(l, store, entry: true, step: true, levels: true);
            if (all) total++;
            if (all || filter.Matches(l, store, entry: true, step: true, levels: false))
                levels[l.Level] = levels.GetValueOrDefault(l.Level) + 1;
            if (all || filter.Matches(l, store, entry: false, step: false, levels: true))
            {
                if (l.Entry == ProgramLogEntry.Preflight) preflight++;
                else if (l.Entry == ProgramLogEntry.Shutdown) shutdown++;
                else if (l.Entry == ProgramLogEntry.Host) host++;
                else if (l.Entry >= 0)
                {
                    while (entries.Count <= l.Entry) entries.Add(0);
                    entries[l.Entry]++;
                }
            }
            if (filter.Entry is >= 0 && filter.Matches(l, store, entry: true, step: false, levels: false))
            {
                var (c, e) = steps.GetValueOrDefault(l.Step);
                steps[l.Step] = (c + 1, e + (l.Level == "Error" ? 1 : 0));
            }
        }
        return new LogFacets(total, levels, entries, steps.Select(kv => new LogStepCount(kv.Key, kv.Value.Count, kv.Value.Errors)).ToList(), preflight, shutdown, host);
    }

    /// <summary>The lines that pass the filter, in their order.</summary>
    public static List<FakeLogEntry> Filter(IReadOnlyList<FakeLogEntry> lines, LogFilter filter, RunStore store)
    {
        if (filter.IsEmpty) return [.. lines];
        var list = new List<FakeLogEntry>(Math.Min(lines.Count, 1024));
        for (var i = 0; i < lines.Count; i++)
            if (filter.Matches(lines[i], store)) list.Add(lines[i]);
        return list;
    }
}
