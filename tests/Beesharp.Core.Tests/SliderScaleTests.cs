using System.ComponentModel.DataAnnotations;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Core.Tests;

public class SliderScaleTests
{
    private sealed class Floats
    {
        [Range(0.0, 1.0)] public double Ratio { get; set; } = 0.25;
        [Range(0, 10)] public decimal Weight { get; set; } = 2.5m;
        public double Free { get; set; } = 42;
        public int Count { get; set; } = 3;
    }

    private static FieldSpec Field(string name) => ParameterSpec.For(typeof(Floats)).Fields.Single(f => f.JsonName == name);

    [Fact]
    public void Float_with_range_uses_the_range_and_a_fractional_step()
    {
        var s = SliderScale.For(Field("ratio"), 0.25);
        Assert.Equal((0d, 1d), (s.Min, s.Max));
        Assert.Equal(0.001, s.Step, 12);
    }

    [Fact]
    public void Decimal_range_of_ten_steps_in_hundredths()
    {
        var s = SliderScale.For(Field("weight"), 2.5);
        Assert.Equal((0d, 10d), (s.Min, s.Max));
        Assert.Equal(0.01, s.Step, 12);
    }

    [Fact]
    public void Float_without_range_gets_a_window_around_the_value_and_a_fractional_step()
    {
        var f = Field("free");
        Assert.Equal(FieldKind.Number, f.Kind);
        var s = SliderScale.For(f, 42);
        Assert.Equal(0, s.Min);
        Assert.Equal(100, s.Max);
        Assert.Equal(0.1, s.Step, 12);
    }

    [Fact]
    public void Window_grows_with_a_large_value_and_never_collapses()
    {
        var big = SliderScale.For(null, null, 640, fractional: true);
        Assert.Equal(1280, big.Max);
        var inverted = SliderScale.For(5, 5, null, fractional: true);
        Assert.True(inverted.Max > inverted.Min);
    }

    [Fact]
    public void Integer_scale_keeps_whole_steps()
    {
        Assert.Equal(1, SliderScale.For(Field("count"), 3).Step);
        Assert.Equal(1, SliderScale.For(0, 1000, 5, fractional: false).Step);
        Assert.Equal(1000, SliderScale.For(0, 100_000, 5, fractional: false).Step);
    }

    [Fact]
    public void Snap_rounds_to_the_step_decimals_and_stays_in_range()
    {
        var s = SliderScale.For(0, 1, null, fractional: true);
        Assert.Equal(0.3, s.Snap(0.1 + 0.2));
        Assert.Equal(1, s.Snap(7));
        Assert.Equal(0, s.Snap(-3));
        Assert.Equal(12, SliderScale.For(0, 100, null, fractional: false).Snap(12.4));
    }

    [Fact]
    public void Thumb_follows_the_value_inside_the_range_and_falls_back_to_the_minimum()
    {
        var s = SliderScale.For(0, 10, null, fractional: true);
        Assert.Equal(4.5, s.Thumb(4.5));
        Assert.Equal(10, s.Thumb(99));
        Assert.Equal(0, s.Thumb(null));
    }
}
