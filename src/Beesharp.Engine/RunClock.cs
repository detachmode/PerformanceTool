using System.Diagnostics;

namespace Beesharp.Services;

/// <summary>
/// The run time of a load run (brain 0027): it starts at zero and stands still while the run is paused. The scheduler starts bees by this
/// time, so a paused run starts no new bees and the remaining bees of the plan come after the pause; bees already in flight finish normally.
/// Thread-safe. A new clock is stopped until <see cref="Start"/>.
/// </summary>
public sealed class RunClock
{
    private readonly object sync = new();
    private readonly Stopwatch watch = new();
    private TimeSpan pausedTotal;
    private TimeSpan? pausedAt;

    /// <summary>Starts the clock (no effect when it already runs or is paused).</summary>
    public void Start()
    {
        lock (sync) if (!watch.IsRunning) watch.Start();
    }

    /// <summary>Run time so far, without the paused time.</summary>
    public TimeSpan Elapsed
    {
        get { lock (sync) return (pausedAt ?? watch.Elapsed) - pausedTotal; }
    }

    public bool IsPaused
    {
        get { lock (sync) return pausedAt is not null; }
    }

    /// <summary>Holds the run time. Returns false when the clock was already paused.</summary>
    public bool Pause()
    {
        lock (sync)
        {
            if (pausedAt is not null) return false;
            pausedAt = watch.Elapsed;
            return true;
        }
    }

    /// <summary>Lets the run time go on from where it stood. Returns false when the clock was not paused.</summary>
    public bool Resume()
    {
        lock (sync)
        {
            if (pausedAt is not { } at) return false;
            pausedTotal += watch.Elapsed - at;
            pausedAt = null;
            return true;
        }
    }

    /// <summary>
    /// Waits until the run time reaches <paramref name="due"/>, waiting through pauses. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is canceled.
    /// </summary>
    public async Task WaitUntilAsync(TimeSpan due, CancellationToken ct)
    {
        // Short slices, so a pause or resume that happens while waiting is seen within a moment; the absolute due time keeps the schedule exact.
        var slice = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var wait = due - Elapsed;
            if (wait <= TimeSpan.Zero && !IsPaused) return;
            await Task.Delay(IsPaused || wait > slice ? slice : wait, ct);
        }
    }
}
