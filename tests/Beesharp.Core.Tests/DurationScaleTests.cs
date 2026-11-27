using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Core.Tests;

public class DurationScaleTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(11, 60)]
    [InlineData(12, 90)]
    [InlineData(29, 600)]
    [InlineData(30, 900)]
    [InlineData(39, 3600)]
    [InlineData(40, 5400)]
    public void Positions_map_to_snap_values(int position, int seconds) => Assert.Equal(seconds, DurationScale.SecondsAt(position));

    [Fact]
    public void The_track_ends_at_six_hours_and_clamps_outside_positions()
    {
        Assert.Equal(21600, DurationScale.MaxSeconds);
        Assert.Equal(21600, DurationScale.SecondsAt(DurationScale.MaxPosition));
        Assert.Equal(21600, DurationScale.SecondsAt(10_000));
        Assert.Equal(5, DurationScale.SecondsAt(-3));
    }

    [Fact]
    public void Values_increase_with_every_position_and_round_trip()
    {
        var last = 0;
        for (var p = 0; p <= DurationScale.MaxPosition; p++)
        {
            var s = DurationScale.SecondsAt(p);
            Assert.True(s > last);
            Assert.Equal(p, DurationScale.PositionFor(s));
            Assert.True(DurationScale.IsOnGrid(s));
            last = s;
        }
    }

    [Fact]
    public void Seconds_have_a_good_share_of_the_track_and_minutes_most_of_it()
    {
        var marks = DurationScale.Marks;
        Assert.Equal(["1 min", "10 min", "1 h", "6 h"], marks.Select(m => m.Label));
        var max = (double)DurationScale.MaxPosition;
        Assert.InRange(marks[0].Position / max, 0.15, 0.35);
        Assert.InRange(marks[1].Position / max, 0.45, 0.7);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(7, 5)]
    [InlineData(8, 10)]
    [InlineData(47, 45)]
    [InlineData(100, 90)]
    [InlineData(105, 90)]    // a tie goes to the lower value
    [InlineData(106, 120)]
    [InlineData(1000, 900)]
    [InlineData(999_999, 21600)]
    public void Typed_values_snap_to_the_nearest_position(int typed, int snapped)
    {
        Assert.Equal(snapped, DurationScale.Snap(typed));
        Assert.Equal(snapped, DurationScale.SecondsAt(DurationScale.PositionFor(typed)));
    }

    [Fact]
    public void A_value_off_the_grid_is_known_as_such_but_still_has_a_position()
    {
        Assert.False(DurationScale.IsOnGrid(47));
        Assert.True(DurationScale.IsOnGrid(45));
        Assert.Equal(DurationScale.PositionFor(45), DurationScale.PositionFor(47));
    }

    [Theory]
    [InlineData(1, "1 s")]
    [InlineData(45, "45 s")]
    [InlineData(60, "1 min")]
    [InlineData(150, "2 min 30 s")]
    [InlineData(3600, "1 h")]
    [InlineData(5400, "1 h 30 min")]
    [InlineData(3661, "1 h 1 min 1 s")]
    [InlineData(21600, "6 h")]
    public void Formats_seconds_as_time(int seconds, string text) => Assert.Equal(text, DurationScale.Format(seconds));

    [Theory]
    [InlineData("90", 90)]
    [InlineData(" 90s ", 90)]
    [InlineData("2m", 120)]
    [InlineData("1h 30m", 5400)]
    [InlineData("1:30:00", 5400)]
    [InlineData("1:30", 90)]
    [InlineData("1.5m", 90)]
    [InlineData("2 min 30 s", 150)]
    [InlineData("1 h 30 min", 5400)]
    [InlineData("45 seconds", 45)]
    [InlineData("1 hour", 3600)]
    public void Parses_typed_text(string text, int seconds)
    {
        Assert.True(DurationScale.TryParse(text, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    [InlineData("1:x")]
    [InlineData("2 weeks")]
    public void Rejects_what_is_not_a_duration(string text) => Assert.False(DurationScale.TryParse(text, out _));

    [Fact]
    public void Formatted_text_parses_back()
    {
        foreach (var s in new[] { 1, 45, 150, 3600, 5400, 3661, 21600 })
        {
            Assert.True(DurationScale.TryParse(DurationScale.Format(s), out var back));
            Assert.Equal(s, back);
        }
    }

    [Fact]
    public void Presets_are_a_short_ascending_list_on_the_grid()
    {
        Assert.InRange(DurationScale.Presets.Count, 4, 8);
        Assert.Equal(DurationScale.Presets.Order(), DurationScale.Presets);
        Assert.All(DurationScale.Presets, p => Assert.True(DurationScale.IsOnGrid(p)));
    }
}
