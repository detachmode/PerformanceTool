namespace PerformanceTool.Client.Components;

/// <summary>
/// DRAFT: the one place for the user-facing words of docs/context.md. One bar = one <b>instance</b> of a scenario;
/// an instance can contain several <b>calls</b>. The draft UI and the details dialog take every occurrence from here.
/// </summary>
public static class Terms
{
    public const string Instance = "actor";
    public const string Instances = "actors";
    public const string InstanceCap = "Actor";
    public const string InstancesCap = "Actors";
    public const string Call = "call";
    public const string Calls = "calls";
    public const string CallsCap = "Calls";
    /// <summary>Short unit for a rate, e.g. "12 actors/min".</summary>
    public const string PerMinuteShort = "actors/min";

    public static string Count(int n) => $"{n:N0} {(n == 1 ? Instance : Instances)}";
    public static string CallCount(int n) => $"{n:N0} {(n == 1 ? Call : Calls)}";
}
