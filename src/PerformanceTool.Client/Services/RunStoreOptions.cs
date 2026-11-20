namespace PerformanceTool.Client.Services;

/// <summary>Limits of the <see cref="RunStore"/>. Settable at any time; the store reads the value whenever an actor is added (brain 0028).</summary>
public sealed class RunStoreOptions
{
    public const int DefaultMaxDetailedActorsPerScenario = 1500;

    /// <summary>
    /// How many finished actors per scenario keep their calls and log lines. Beyond it the detail of successful actors is dropped first
    /// (oldest first), then slow, then failed. The tiny actor records are always kept.
    /// </summary>
    public int MaxDetailedActorsPerScenario { get; set; } = DefaultMaxDetailedActorsPerScenario;
}
