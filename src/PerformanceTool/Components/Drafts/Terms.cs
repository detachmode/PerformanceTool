namespace PerformanceTool.Components.Drafts;

/// <summary>
/// DRAFT: the one place for the user-facing word for "one call of the scenario code" (docs/context.md: "execution").
/// It may be renamed soon, so the draft UI and the details dialog take every occurrence from here.
/// </summary>
public static class Terms
{
    public const string Execution = "execution";
    public const string Executions = "executions";
    public const string ExecutionCap = "Execution";
    public const string ExecutionsCap = "Executions";
    /// <summary>Short unit for a rate, e.g. "12 exec/min".</summary>
    public const string PerMinuteShort = "exec/min";

    public static string Count(int n) => $"{n:N0} {(n == 1 ? Execution : Executions)}";
}
