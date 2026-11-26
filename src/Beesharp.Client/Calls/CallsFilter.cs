using System.Globalization;
using Microsoft.AspNetCore.Components;
using Beesharp.Client.Chart;

namespace Beesharp.Client.Calls;

/// <summary>
/// URL-synced filter state of the Calls analysis page (<c>/calls</c>, brain 0060): selected endpoint, scenario filter (1-based in URL),
/// outcome filter, duration window (<see cref="MinMs"/>/<see cref="MaxMs"/>), time window (<see cref="FromSec"/>/<see cref="ToSec"/>),
/// call sort order and endpoint search.
/// </summary>
public sealed record CallsFilter
{
    public static readonly CallsFilter None = new();

    public string? Endpoint { get; init; }
    public int? Entry { get; init; }
    public BeeShow Outcome { get; init; }
    public double? MinMs { get; init; }
    public double? MaxMs { get; init; }
    public double? FromSec { get; init; }
    public double? ToSec { get; init; }
    public string Sort { get; init; } = "slowest";
    public string Search { get; init; } = "";
    public double? TargetMs { get; init; }

    public bool IsDefaultDetailFilter =>
        Outcome == BeeShow.All && MinMs is null && MaxMs is null && FromSec is null && ToSec is null;

    public CallsFilter WithEndpoint(string? endpoint) =>
        this with { Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(), FromSec = null, ToSec = null, MaxMs = null };

    public CallsFilter WithEntry(int? entry) => this with { Entry = entry };
    public CallsFilter WithOutcome(BeeShow outcome) => this with { Outcome = outcome };
    public CallsFilter WithMinMs(double? minMs) => this with { MinMs = minMs is > 0 ? minMs : null };
    public CallsFilter WithDurationRange(double? minMs, double? maxMs) => this with { MinMs = minMs, MaxMs = maxMs };
    public CallsFilter WithTimeWindow(double? fromSec, double? toSec) => this with { FromSec = fromSec, ToSec = toSec };
    public CallsFilter WithSort(string? sort) => this with { Sort = NormalizeSort(sort) };
    public CallsFilter WithSearch(string? search) => this with { Search = search ?? "" };
    public CallsFilter WithTargetMs(double? targetMs) => this with { TargetMs = targetMs is > 0 && !double.IsNaN(targetMs.Value) && !double.IsInfinity(targetMs.Value) ? targetMs : null };
    public CallsFilter ClearDetailFilters() => this with { Outcome = BeeShow.All, MinMs = null, MaxMs = null, FromSec = null, ToSec = null };

    public IReadOnlyList<KeyValuePair<string, string>> ToQuery()
    {
        var q = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(Endpoint)) q.Add(new("endpoint", Endpoint));
        if (Entry is { } e && e >= 0) q.Add(new("scenario", (e + 1).ToString(CultureInfo.InvariantCulture)));
        if (Outcome != BeeShow.All) q.Add(new("show", Outcome == BeeShow.Failed ? "failed" : "slow"));
        if (MinMs is { } min) q.Add(new("minMs", min.ToString("0.##", CultureInfo.InvariantCulture)));
        if (MaxMs is { } max) q.Add(new("maxMs", max.ToString("0.##", CultureInfo.InvariantCulture)));
        if (FromSec is { } from) q.Add(new("from", from.ToString("0.##", CultureInfo.InvariantCulture)));
        if (ToSec is { } to) q.Add(new("to", to.ToString("0.##", CultureInfo.InvariantCulture)));
        if (!string.Equals(Sort, "slowest", StringComparison.Ordinal)) q.Add(new("sort", Sort));
        if (Search.Trim().Length > 0) q.Add(new("q", Search.Trim()));
        if (TargetMs is { } target && target > 0) q.Add(new("target", target.ToString("0.##", CultureInfo.InvariantCulture)));
        return q;
    }

    public string ToQueryString() => string.Join('&', ToQuery().Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    public string Href()
    {
        var qs = ToQueryString();
        return qs.Length == 0 ? "calls" : "calls?" + qs;
    }

    public static CallsFilter FromQuery(
        string? endpoint,
        string? scenario,
        string? show,
        string? minMs,
        string? maxMs,
        string? from,
        string? to,
        string? sort,
        string? q,
        string? target = null)
    {
        static int? OneBasedIndex(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n - 1 : null;
        static double? Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;

        return new CallsFilter
        {
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(),
            Entry = OneBasedIndex(scenario),
            Outcome = (show ?? "").Trim().ToLowerInvariant() switch
            {
                "slow" => BeeShow.Slow,
                "failed" => BeeShow.Failed,
                _ => BeeShow.All,
            },
            MinMs = Num(minMs) is { } lo and >= 0 ? lo : null,
            MaxMs = Num(maxMs) is { } hi and > 0 ? hi : null,
            FromSec = Num(from) is { } fs and >= 0 ? fs : null,
            ToSec = Num(to) is { } ts and >= 0 ? ts : null,
            Sort = NormalizeSort(sort),
            Search = q ?? "",
            TargetMs = Num(target) is { } tg and > 0 ? tg : null,
        };
    }

    private static string NormalizeSort(string? sort) => (sort ?? "").Trim().ToLowerInvariant() switch
    {
        "time" => "time",
        "failed" => "failed",
        _ => "slowest",
    };

    public bool SameAs(CallsFilter other) => ToQueryString() == other.ToQueryString();
}

/// <summary>Navigates from other pages (e.g. a bee's Calls tab) straight to an endpoint on <c>/calls</c>, and remembers the last filter.</summary>
public sealed class CallsLinks(NavigationManager nav)
{
    public CallsFilter Last { get; set; } = CallsFilter.None;

    public void Show(string? endpoint = null, int? entry = null, BeeShow outcome = BeeShow.All) =>
        Show(CallsFilter.None.WithEndpoint(endpoint).WithEntry(entry).WithOutcome(outcome));

    public void Show(CallsFilter filter)
    {
        Last = filter;
        nav.NavigateTo(filter.Href());
    }
}
