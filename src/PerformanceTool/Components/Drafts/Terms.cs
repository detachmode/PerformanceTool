namespace PerformanceTool.Components.Drafts;

/// <summary>
/// DRAFT: the one place for the user-facing words of docs/context.md. One bar = one <b>instance</b> of a scenario;
/// an instance can contain several <b>calls</b>. The draft UI and the details dialog take every occurrence from here.
/// </summary>
public static class Terms
{
    public const string Instance = "instance";
    public const string Instances = "instances";
    public const string InstanceCap = "Instance";
    public const string InstancesCap = "Instances";
    public const string Call = "call";
    public const string Calls = "calls";
    public const string CallsCap = "Calls";
    /// <summary>Short unit for a rate, e.g. "12 inst/min".</summary>
    public const string PerMinuteShort = "inst/min";

    public static string Count(int n) => $"{n:N0} {(n == 1 ? Instance : Instances)}";
    public static string CallCount(int n) => $"{n:N0} {(n == 1 ? Call : Calls)}";
}
