namespace PerformanceTool.Client.Services;

/// <summary>Limits of the <see cref="RunStore"/>. Settable at any time; the store reads the value whenever a bee is added (brain 0028).</summary>
public sealed class RunStoreOptions
{
    public const int DefaultMaxDetailedBeesPerScenario = 1500;

    /// <summary>
    /// How many finished bees per scenario keep their calls and log lines. Beyond it the detail of successful bees is dropped first
    /// (oldest first), then slow, then failed. The tiny bee records are always kept.
    /// </summary>
    public int MaxDetailedBeesPerScenario { get; set; } = DefaultMaxDetailedBeesPerScenario;
}
