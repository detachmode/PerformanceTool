using System.Text.RegularExpressions;
using Beesharp.Client.Chart;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Logs;

/// <summary>
/// What the Logs page shows (brain 0057): levels, one scenario entry, one step of it, one bee, an outcome of the bee and a search text. Immutable;
/// every choice returns a new filter. A search that is only a bee number ("B199") filters by that bee instead of searching the text.
/// It reads from and writes to the address of the page (<see cref="ToQuery"/>, <see cref="FromQuery"/>), so entry points are plain links. Entries, steps
/// and bees are numbered from 1 there, like everywhere the user reads them.
/// </summary>
public sealed record LogFilter
{
    public static readonly LogFilter None = new();

    private static readonly Regex BeeNumber = new(@"^\s*[Bb](\d+)\s*$", RegexOptions.Compiled);

    /// <summary>Level names (<see cref="FakeLogEntry.Levels"/>) in their order; empty = every level.</summary>
    public IReadOnlyList<string> Levels { get; init; } = [];
    public int? Entry { get; init; }
    /// <summary>A step of <see cref="Entry"/>; only means something together with it.</summary>
    public int? Step { get; init; }
    /// <summary>A bee by its sequence (B7 is sequence 6).</summary>
    public int? Bee { get; init; }
    /// <summary>Only the logs of the failed (or slow) bees.</summary>
    public BeeShow Outcome { get; init; }
    public string Search { get; init; } = "";

    public bool IsEmpty => Levels.Count == 0 && Entry is null && Step is null && Bee is null && Outcome == BeeShow.All && Search.Trim().Length == 0;

    /// <summary>The search split into the bee it names (when it is just a bee number) and the text to look for.</summary>
    public (int? Bee, string Text) SearchParts
    {
        get
        {
            var m = BeeNumber.Match(Search);
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 1 ? (n - 1, "") : (null, Search.Trim());
        }
    }

    /// <summary>The bee the filter ends up with: the chosen one, or the one the search names.</summary>
    public int? EffectiveBee => Bee ?? SearchParts.Bee;

    /// <summary>True when the search names a bee that is not the chosen bee: nothing can match.</summary>
    public bool Contradicts => Bee is { } b && SearchParts.Bee is { } sb && b != sb;

    public LogFilter ToggleLevel(string level)
    {
        var set = Levels.ToList();
        if (!set.Remove(level)) set.Add(level);
        set.Sort((a, b) => FakeLogEntry.Rank(a).CompareTo(FakeLogEntry.Rank(b)));
        return this with { Levels = set };
    }

    public LogFilter WithLevels(IEnumerable<string> levels) => this with { Levels = levels.Distinct().OrderBy(FakeLogEntry.Rank).ToList() };
    public LogFilter WithEntry(int? entry) => this with { Entry = entry, Step = entry == Entry ? Step : null };
    public LogFilter WithStep(int? step) => this with { Step = Entry is null ? null : step };
    public LogFilter WithBee(int? bee) => this with { Bee = bee };
    public LogFilter WithOutcome(BeeShow outcome) => this with { Outcome = outcome };
    public LogFilter WithSearch(string? search) => this with { Search = search ?? "" };
    public LogFilter ToggleEntry(int entry) => WithEntry(Entry == entry ? null : entry);
    public LogFilter ToggleStep(int step) => WithStep(Step == step ? null : step);

    /// <summary>Whether a line passes every part of the filter.</summary>
    public bool Matches(FakeLogEntry l, RunStore store) => Matches(l, store, entry: true, step: true, levels: true);

    /// <summary>As <see cref="Matches(FakeLogEntry, RunStore)"/>, leaving out the groups that a count of its own ignores (the group's choice must not hide its alternatives).</summary>
    public bool Matches(FakeLogEntry l, RunStore store, bool entry, bool step, bool levels)
    {
        if (Contradicts) return false;
        if (entry && Entry is { } e && l.Entry != e) return false;
        if (step && Step is { } st && l.Step != st) return false;
        if (EffectiveBee is { } b && l.Sequence != b) return false;
        if (levels && Levels.Count > 0 && !Levels.Contains(l.Level)) return false;
        if (Outcome != BeeShow.All)
        {
            var o = store.OutcomeOf(l.Sequence);
            if (Outcome == BeeShow.Failed ? o != BeeOutcome.Failed : o != BeeOutcome.Slow) return false;
        }
        var text = SearchParts.Text;
        if (text.Length > 0
            && !l.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
            && (l.Exception is null || !l.Exception.Contains(text, StringComparison.OrdinalIgnoreCase))
            && !$"B{l.Sequence + 1}".Contains(text, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ---------- the address of the page ----------

    /// <summary>The query string parts, in a fixed order; empty for no filter.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToQuery()
    {
        var q = new List<KeyValuePair<string, string>>();
        if (Entry is { } e) q.Add(new("scenario", (e + 1).ToString()));
        if (Step is { } s && Entry is not null) q.Add(new("step", (s + 1).ToString()));
        if (Bee is { } b) q.Add(new("bee", (b + 1).ToString()));
        if (Levels.Count > 0) q.Add(new("level", string.Join(',', Levels.Select(l => l.ToLowerInvariant()))));
        if (Outcome != BeeShow.All) q.Add(new("bees", Outcome == BeeShow.Failed ? "failed" : "slow"));
        if (Search.Trim().Length > 0) q.Add(new("q", Search.Trim()));
        return q;
    }

    /// <summary>"scenario=2&amp;level=error", escaped; empty for no filter.</summary>
    public string ToQueryString() => string.Join('&', ToQuery().Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    /// <summary>The address of the Logs page showing this filter, relative to the app's base address.</summary>
    public string Href() => IsEmpty ? "logs" : "logs?" + ToQueryString();

    public static LogFilter FromQuery(string? scenario, string? step, string? bee, string? level, string? bees, string? q)
    {
        static int? Number(string? s) => int.TryParse(s, out var n) && n >= 1 ? n - 1 : null;
        var entry = Number(scenario);
        var levels = (level ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(LevelName).Where(l => l is not null).Select(l => l!);
        return None with
        {
            Entry = entry,
            Step = entry is null ? null : Number(step),
            Bee = Number(bee),
            Levels = levels.Distinct().OrderBy(FakeLogEntry.Rank).ToList(),
            Outcome = (bees ?? "").ToLowerInvariant() switch { "failed" => BeeShow.Failed, "slow" => BeeShow.Slow, _ => BeeShow.All },
            Search = q ?? "",
        };
    }

    /// <summary>"warn", "warning", "WRN" and the like to the level name; null for something unknown.</summary>
    public static string? LevelName(string s) => s.Trim().ToLowerInvariant() switch
    {
        "trace" or "trc" => "Trace",
        "debug" or "dbg" => "Debug",
        "information" or "info" or "inf" => "Information",
        "warning" or "warn" or "wrn" => "Warning",
        "error" or "err" or "critical" or "ftl" => "Error",
        _ => null,
    };

    /// <summary>Same parts in the same order = the same filter (the record's own equality would compare the level list by reference).</summary>
    public bool SameAs(LogFilter other) => ToQueryString() == other.ToQueryString();
}
