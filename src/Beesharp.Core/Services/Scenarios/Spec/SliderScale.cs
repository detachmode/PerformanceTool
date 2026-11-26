using System.Globalization;

namespace Beesharp.Services.Scenarios.Spec;

/// <summary>
/// The range and step of the slider the generated editor shows next to a numeric field (brain 0007, brain 0066).
/// A <see cref="FieldSpec.Min"/>/<see cref="FieldSpec.Max"/> from <c>[Range]</c> is used as is; without a range the
/// slider spans a sensible window around the current value. Floating-point fields get a fractional step so the
/// thumb can land between whole numbers.
/// </summary>
public readonly record struct SliderScale(double Min, double Max, double Step)
{
    /// <summary>Number of steps a floating-point slider aims for across its span (a power of ten step at least this fine).</summary>
    private const double FractionalSteps = 200;

    /// <summary>The finest step a slider uses; typed values may be finer.</summary>
    private const double SmallestStep = 0.0001;

    /// <summary>Scale for a field: whole numbers when <paramref name="fractional"/> is false, fractional steps otherwise.</summary>
    /// <param name="min">The declared minimum, or null.</param>
    /// <param name="max">The declared maximum, or null.</param>
    /// <param name="current">The current value, used to pick a window when no range is declared.</param>
    /// <param name="fractional">True for <see cref="double"/>, <see cref="float"/> and <see cref="decimal"/> fields.</param>
    public static SliderScale For(double? min, double? max, double? current, bool fractional)
    {
        var cur = current ?? 0;
        var lo = min ?? Math.Min(0, cur);
        var hi = max ?? Math.Max(lo + 100, cur > 0 ? Math.Ceiling(cur * 2 / 10.0) * 10 : 100);
        if (hi <= lo) hi = lo + 100;
        var span = hi - lo;
        double step;
        if (fractional)
            step = Math.Max(SmallestStep, Math.Pow(10, Math.Floor(Math.Log10(span / FractionalSteps))));
        else
            step = span <= 1000 ? 1 : Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(span / 100.0))));
        return new SliderScale(lo, hi, step);
    }

    /// <summary>Scale for a parameter field (see <see cref="For(double?, double?, double?, bool)"/>).</summary>
    public static SliderScale For(FieldSpec field, double? current) =>
        For(field.Min, field.Max, current, fractional: field.Kind == FieldKind.Number);

    /// <summary>Rounds a slid value to the step's decimals, so 0.1 + 0.2 never shows as 0.30000000000000004.</summary>
    public double Snap(double value)
    {
        var decimals = Step >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(Step) - 1e-9);
        return Math.Round(Math.Clamp(value, Min, Max), Math.Min(decimals, 8), MidpointRounding.AwayFromZero);
    }

    /// <summary>The value for the slider's thumb: the current value kept inside the range, or the minimum when empty.</summary>
    public double Thumb(double? current) => Math.Clamp(current ?? Min, Min, Max);

    /// <summary>Invariant text of a bound for an HTML attribute.</summary>
    public static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);
}
