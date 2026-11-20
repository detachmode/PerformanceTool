namespace PerformanceTool.Services;

/// <summary>
/// Categorical colours for scenario entries, in fixed order (never cycled — that is why a plan has at most
/// <see cref="LoadPlan.MaxEntries"/> entries). Validated for colour-blind separation and 3:1 contrast against
/// the dark card surface #2a2534 (and page #221d2d). Failed runs use <see cref="Error"/> plus a hatch texture. See docs/brain/0002.
/// </summary>
public static class SeriesPalette
{
    public static readonly string[] Colors = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#0ca30c", "#9085e9"];
    public const string Error = "#d03b3b";

    public static string For(int entry) => entry >= 0 && entry < Colors.Length ? Colors[entry] : "#8b96c0";
}
