using Beesharp.Client.Shared;

namespace Beesharp.Client.Runs;

/// <summary>
/// The one quiet line after a bee in the bee list of the scenario page (brain 0057, 0016): the error of a failed bee and the step it failed at, the slow call of a slow bee,
/// and "Step 3 of 5 · Open product" for a bee in flight. A successful bee says nothing.
/// </summary>
public static class BeeLine
{
    /// <summary>The line of a finished bee. <paramref name="detail"/> (its calls) may be missing: older bees drop it first (brain 0028).</summary>
    public static string? Of(in BeeRecord bee, BeeDetail? detail, ScenarioSteps steps)
    {
        if (bee.Failed)
        {
            var text = FirstLine(bee.Error);
            var at = steps.Any && bee.FailedStep >= 0 ? " · at " + steps.Name(bee.FailedStep) : "";
            return text.Length == 0 ? at.Length > 0 ? at[3..] : null : text + at;
        }
        if (bee.SlowCalls > 0)
        {
            FakeCall? worst = null;
            if (detail is not null)
                foreach (var c in detail.Calls)
                    if (c.Slow && (worst is null || c.DurationMs > worst.DurationMs)) worst = c;
            return worst is null ? "slow" : $"slow: {worst.Name} {Format.Ms(worst.DurationMs)}";
        }
        return null;
    }

    /// <summary>The line of a bee in flight: its step (when the scenario has steps) or the call it is making now.</summary>
    public static string? Of(RunningBee bee, ScenarioSteps steps, double elapsedSeconds)
    {
        var ms = Math.Max(0, (elapsedSeconds - bee.StartedAt) * 1000);
        if (steps.Any) return StepLogic.PositionOf(steps, bee, ms);
        FakeCall? last = null;
        foreach (var c in bee.Calls)
            if (!c.Skipped && c.OffsetMs <= ms) last = c;
        return last?.Name;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Trim();
        var nl = line.IndexOfAny(['\r', '\n']);
        return nl >= 0 ? line[..nl].Trim() : line;
    }
}
