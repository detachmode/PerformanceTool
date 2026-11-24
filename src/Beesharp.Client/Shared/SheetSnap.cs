namespace Beesharp.Client.Shared;

/// <summary>Where a bottom sheet rests.</summary>
public enum SheetStop { Closed, Half, Full }

/// <summary>
/// What a bottom sheet does when the finger lets go (brain 0059, phone): it settles at the nearest of its stops, looking a little ahead along the swipe,
/// so a short drag falls back, a flick carries on, and a swipe down from half height closes it. A sheet that cannot grow has no full stop.
/// </summary>
public static class SheetSnap
{
    /// <summary>How far ahead the swipe is projected, in milliseconds.</summary>
    public const double LookAheadMs = 160;

    /// <param name="height">The sheet's height when released, in px.</param>
    /// <param name="velocityUp">The speed of the swipe in px per ms; positive = upwards (the sheet grows).</param>
    /// <param name="half">Height of the half stop (the resting height of a sheet that cannot grow).</param>
    /// <param name="full">Height of the full stop.</param>
    /// <param name="resizable">The sheet can grow to full height.</param>
    public static SheetStop Decide(double height, double velocityUp, double half, double full, bool resizable)
    {
        var projected = height + velocityUp * LookAheadMs;
        var best = SheetStop.Closed;
        var bestDistance = Math.Abs(projected);
        var dHalf = Math.Abs(projected - half);
        if (dHalf < bestDistance) { best = SheetStop.Half; bestDistance = dHalf; }
        if (resizable && Math.Abs(projected - full) < bestDistance) best = SheetStop.Full;
        return best;
    }
}
