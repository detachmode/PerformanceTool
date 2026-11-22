namespace Beesharp.Client.Runs;

// Scenario steps (brain 0056, UI draft with fake data): a scenario declares named steps before the run, so a bee in flight is at "step 2 of 4"
// and a failed bee failed at one step. Everything here is small and pure: the data stays tiny (a step index per call, one per failed bee, brain 0021).

/// <summary>The declared steps of one scenario entry, in order. <see cref="None"/> (no steps) means the scenario looks exactly like before.</summary>
public sealed record ScenarioSteps(IReadOnlyList<string> Names, IReadOnlyList<int>? Repeats = null)
{
    public static readonly ScenarioSteps None = new([]);

    public int Count => Names.Count;
    public bool Any => Names.Count > 0;
    public string Name(int step) => step >= 0 && step < Names.Count ? Names[step] : "";

    /// <summary>How many times a bee runs the step in a row (brain 0056, feedback): declared up front like the step itself; 1 for a step that runs once. Repetitions are consecutive in this draft.</summary>
    public int Times(int step) => Repeats is not null && step >= 0 && step < Repeats.Count ? Math.Max(1, Repeats[step]) : 1;

    /// <summary>1st, 2nd, 3rd, 4th ...</summary>
    public static string Ordinal(int n) => n + (n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>"Step 2 of 4 · Search": where a bee in flight is, shown quietly wherever such a bee appears.</summary>
    public string Position(int step, int rep = -1) => $"Step {step + 1} of {Count} · {Name(step)}" + (rep >= 0 && Times(step) > 1 ? $" ({rep + 1} of {Times(step)})" : "");
}

/// <summary>How a step looks in a bee's stepper: done (check), the one it failed at (red), the one in flight now, or not reached (greyed out).</summary>
public enum StepState : byte { Done, Failed, Current, Pending }

/// <summary>One step of one bee: its state, the time it took (first call start to last call end), its calls and, when it failed, the error text.</summary>
public sealed record StepView(int Index, string Name, StepState State, double DurationMs, IReadOnlyList<FakeCall> Calls, string? Error, int SlowCalls, bool Canceled = false, int Times = 1, int FailedRep = -1, int CurrentRep = -1)
{
    /// <summary>The step runs several times in a row: it shows once, with its repetitions folded out like its calls.</summary>
    public bool Repeats => Times > 1;
    public bool HasCalls => Calls.Count > 0;
}

/// <summary>What the scenario popup filters its bees by: those that failed at a step, or those in flight at a step now (brain 0029, 0056).</summary>
public readonly record struct StepFilter(int Step, bool InFlight)
{
    /// <summary>Whether a finished bee passes: only a failed bee that failed at this step (an in-flight filter lists no finished bees).</summary>
    public bool Includes(in BeeRecord bee) => !InFlight && bee.Failed && bee.FailedStep == Step;
}

public static class StepLogic
{
    /// <summary>The step a bee in flight is in at <paramref name="elapsedMs"/>: the step of the last call that has started (0 before the first).</summary>
    public static int CurrentStep(IReadOnlyList<FakeCall> calls, double elapsedMs)
    {
        var step = 0;
        foreach (var c in calls)
            if (!c.Skipped && c.OffsetMs <= elapsedMs) step = c.Step;
        return step;
    }

    /// <summary>The repetition (0 based) of the step a bee in flight is in at <paramref name="elapsedMs"/>.</summary>
    public static int CurrentRep(IReadOnlyList<FakeCall> calls, double elapsedMs)
    {
        var rep = 0;
        foreach (var c in calls)
            if (!c.Skipped && c.OffsetMs <= elapsedMs) rep = c.Rep;
        return rep;
    }

    /// <summary>"Step 3 of 5 · Open product (2 of 5)" for a bee in flight.</summary>
    public static string PositionOf(ScenarioSteps steps, IReadOnlyList<FakeCall> calls, double elapsedMs) =>
        steps.Position(CurrentStep(calls, elapsedMs), CurrentRep(calls, elapsedMs));

    /// <summary>The repetition (0 based) of the first failed call, or -1.</summary>
    public static int FailedRep(IReadOnlyList<FakeCall> calls)
    {
        foreach (var c in calls)
            if (c.Failed) return c.Rep;
        return -1;
    }

    /// <summary>The step a finished bee failed at (its first failed call), or -1 when no call failed.</summary>
    public static int FailedStep(IReadOnlyList<FakeCall> calls)
    {
        foreach (var c in calls)
            if (c.Failed) return c.Step;
        return -1;
    }

    /// <summary>The steps of a finished bee (done, the failed one, not reached).</summary>
    public static List<StepView> ForBee(ScenarioSteps steps, IReadOnlyList<FakeCall> calls) => Build(steps, calls, double.PositiveInfinity);

    /// <summary>The steps of a bee in flight: steps before the current one are done, the current one is highlighted, later ones are not reached. Only calls that have started count.</summary>
    public static List<StepView> ForRunning(ScenarioSteps steps, IReadOnlyList<FakeCall> calls, double elapsedMs) => Build(steps, calls, elapsedMs);

    private static List<StepView> Build(ScenarioSteps steps, IReadOnlyList<FakeCall> calls, double elapsedMs)
    {
        var running = !double.IsPositiveInfinity(elapsedMs);
        // A bee in flight has not failed yet, even though the fake playthrough already knows how it ends.
        var failed = running ? -1 : FailedStep(calls);
        var current = running ? CurrentStep(calls, elapsedMs) : -1;
        var list = new List<StepView>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var mine = calls.Where(c => c.Step == i && !c.Skipped && c.OffsetMs <= elapsedMs).ToList();
            double start = mine.Count == 0 ? 0 : mine.Min(c => c.OffsetMs);
            double end = mine.Count == 0 ? 0 : mine.Max(c => Math.Min(c.OffsetMs + c.DurationMs, elapsedMs));
            var failedCall = running ? null : mine.FirstOrDefault(c => c.Failed);
            var state = failed == i ? StepState.Failed
                : running ? (i < current ? StepState.Done : i == current ? StepState.Current : StepState.Pending)
                : failed >= 0 && i > failed ? StepState.Pending
                : mine.Count == 0 ? StepState.Pending : StepState.Done;
            var canceled = failedCall is not null && Beesharp.Services.ExecutionResult.IsCanceledError(failedCall.Error);
            list.Add(new StepView(i, steps.Names[i], state, mine.Count == 0 ? 0 : Math.Max(0, end - start), mine, failedCall?.Error, mine.Count(c => c.Slow), canceled, steps.Times(i), failedCall?.Rep ?? -1, running && mine.Count > 0 ? mine.Max(c => c.Rep) : -1));
        }
        return list;
    }

    /// <summary>How many bees in flight are in each step right now.</summary>
    public static int[] InFlightCounts(ScenarioSteps steps, IReadOnlyList<RunningBee> running, double elapsed)
    {
        var counts = new int[steps.Count];
        foreach (var r in running)
        {
            var step = CurrentStep(r.Calls, Math.Max(0, (elapsed - r.StartedAt) * 1000));
            if (step >= 0 && step < counts.Length) counts[step]++;
        }
        return counts;
    }

    /// <summary>The bees in flight that pass the filter: those in the filtered step now; a failed-at filter lists none (they have not failed).</summary>
    public static IReadOnlyList<RunningBee> FilterRunning(IReadOnlyList<RunningBee> running, StepFilter? filter, double elapsed)
    {
        if (filter is not { } f) return running;
        if (!f.InFlight) return [];
        return running.Where(r => CurrentStep(r.Calls, Math.Max(0, (elapsed - r.StartedAt) * 1000)) == f.Step).ToList();
    }

    /// <summary>The finished bees that pass the filter, in the same order. The input list is returned unchanged without a filter.</summary>
    public static IReadOnlyList<BeeRecord> FilterFinished(IReadOnlyList<BeeRecord> bees, int count, StepFilter? filter)
    {
        if (filter is not { } f) return bees;
        var list = new List<BeeRecord>();
        for (var i = 0; i < count; i++)
            if (f.Includes(bees[i])) list.Add(bees[i]);
        return list;
    }

    /// <summary>The step most bees failed at: its index and count, or null when no bee failed at a declared step.</summary>
    public static (int Step, int Count)? MostFailed(IReadOnlyList<int> failedPerStep)
    {
        var best = -1;
        for (var i = 0; i < failedPerStep.Count; i++)
            if (failedPerStep[i] > 0 && (best < 0 || failedPerStep[i] > failedPerStep[best])) best = i;
        return best < 0 ? null : (best, failedPerStep[best]);
    }
}
