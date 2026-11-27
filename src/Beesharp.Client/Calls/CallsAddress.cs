using System.Globalization;
using Beesharp.Client.Runs;
using Beesharp.Services.Scenarios;

namespace Beesharp.Client.Calls;

/// <summary>
/// Where the user came from when they opened a call out of a bee (<c>scenario:1:4668</c>: scenario 1, bee B4668, both as the UI numbers them). The Calls page shows
/// it as a small "‹ B4668 · Checkout" link at the top (brain 0070). The address parameter is <c>origin</c>, not <c>from</c>, because <c>from</c> is the time window.
/// </summary>
public readonly record struct CallsOrigin(int Entry, int Bee)
{
    public string ToParam() => "scenario:" + (Entry + 1).ToString(CultureInfo.InvariantCulture) + ":" + (Bee + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The scenario page with this bee open on its Calls tab: where "‹ B4668" leads when history cannot go back.</summary>
    public string ScenarioHref() => ScenarioLink.Href(Entry, new ScenarioFilter(Bee: Bee), BeeTab.Calls);

    public static CallsOrigin? Parse(string? value)
    {
        var p = (value ?? "").Split(':');
        return p.Length == 3 && p[0] == "scenario"
            && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var e) && e >= 1
            && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b >= 1
            ? new CallsOrigin(e - 1, b - 1)
            : null;
    }
}

/// <summary>
/// The stable address of one call, the same in Fake and Live mode (brain 0070): the bee, the step and repetition it belongs to and its offset in the bee, e.g. <c>4668.2.0.153120</c>
/// (bee B4668, step 2, repetition 0, 15312.0 ms after the bee started). Call ids are not stable: Live mode numbers them in the database, so the id of a call changes with the run it is read from.
/// </summary>
public static class CallKey
{
    public static string Of(int sequence, int step, int rep, double offsetMs) =>
        string.Join('.',
            (sequence + 1).ToString(CultureInfo.InvariantCulture),
            step.ToString(CultureInfo.InvariantCulture),
            rep.ToString(CultureInfo.InvariantCulture),
            ((long)Math.Round(offsetMs * 10, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));

    public static string Of(CallSampleDto call) => Of(call.Sequence, call.Step, call.Rep, call.OffsetMs);

    /// <summary>The call of a bee as the bee view lists it.</summary>
    public static string Of(int sequence, FakeCall call) => Of(sequence, call.Step, call.Rep, call.OffsetMs);

    /// <summary>Is this a call key at all (anything else in the address is ignored).</summary>
    public static bool IsValid(string? key)
    {
        var p = (key ?? "").Split('.');
        return p.Length == 4 && p.All(x => x.Length > 0 && x.All(char.IsAsciiDigit));
    }

    /// <summary>The endpoint a call of a bee belongs to (what the Calls page lists it under).</summary>
    public static string EndpointOf(FakeCall call) =>
        string.IsNullOrWhiteSpace(call.Endpoint) ? EndpointNormalizer.NormalizeCallName(call.Name) : call.Endpoint;
}

/// <summary>What the Calls page keeps in its address besides the filter (brain 0070): the selected call, where the user came from, and the bee side panel.</summary>
public sealed record CallsView(string? Call = null, CallsOrigin? Origin = null, int? OpenBee = null, BeeTab Tab = BeeTab.Steps)
{
    public static readonly CallsView None = new();
    public bool PanelOpen => OpenBee is not null;
}

/// <summary>The address of the Calls page with its view: <c>calls?endpoint=…&amp;sort=time&amp;call=4668.2.0.153120&amp;origin=scenario:1:4668&amp;open=4668&amp;beeTab=calls</c>.</summary>
public static class CallsAddress
{
    public static string Href(CallsFilter filter, CallsView view)
    {
        var parts = new List<string>();
        var q = filter.ToQueryString();
        if (q.Length > 0) parts.Add(q);
        if (CallKey.IsValid(view.Call)) parts.Add("call=" + view.Call);
        if (view.Origin is { } o) parts.Add("origin=" + o.ToParam());
        if (view.OpenBee is { } b)
        {
            parts.Add("open=" + (b + 1).ToString(CultureInfo.InvariantCulture));
            if (ScenarioLink.BeeTabName(view.Tab) is { } tab) parts.Add("beeTab=" + tab);
        }
        return parts.Count == 0 ? "calls" : "calls?" + string.Join('&', parts);
    }

    /// <summary>The view parts of the address. Malformed values mean "not there".</summary>
    public static CallsView Parse(string? call, string? origin, string? open, string? beeTab)
    {
        int? bee = int.TryParse(open, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n - 1 : null;
        return new CallsView(
            CallKey.IsValid(call) ? call : null,
            CallsOrigin.Parse(origin),
            bee,
            bee is null ? BeeTab.Steps : ScenarioLink.ParseBeeTabName(beeTab));
    }
}
