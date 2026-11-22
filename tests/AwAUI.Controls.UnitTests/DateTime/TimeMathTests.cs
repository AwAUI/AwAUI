using System;
using System.Globalization;
using System.Linq;
using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests.DateTimePrimitives;

/// <summary>Time-of-day conversion, snapping, formatting, and parsing.</summary>
public class TimeMathTests
{
    [Theory]
    [InlineData(0, 12, false)]
    [InlineData(1, 1, false)]
    [InlineData(11, 11, false)]
    [InlineData(12, 12, true)]
    [InlineData(13, 1, true)]
    [InlineData(23, 11, true)]
    public void Twenty_four_hour_values_convert_to_twelve_hour(int hour24, int expectedHour, bool expectedPm)
    {
        int hour = TimeMath.ToTwelveHour(hour24, out bool isPm);

        Assert.Equal(expectedHour, hour);
        Assert.Equal(expectedPm, isPm);
    }

    [Theory]
    [InlineData(12, false, 0)]
    [InlineData(12, true, 12)]
    [InlineData(1, false, 1)]
    [InlineData(1, true, 13)]
    [InlineData(11, true, 23)]
    public void Twelve_hour_values_convert_back(int hour12, bool isPm, int expected)
    {
        Assert.Equal(expected, TimeMath.ToTwentyFourHour(hour12, isPm));
    }

    [Fact]
    public void Minute_values_respect_the_increment()
    {
        Assert.Equal(new[] { 0, 15, 30, 45 }, TimeMath.MinuteValues(15).ToArray());
        Assert.Equal(60, TimeMath.MinuteValues(1).Count);
        Assert.Equal(new[] { 0 }, TimeMath.MinuteValues(120).ToArray()); // clamped to a whole hour
    }

    [Fact]
    public void Snapping_floors_the_minute_and_wraps_into_the_day()
    {
        Assert.Equal(new TimeSpan(14, 30, 0), TimeMath.SnapMinutes(new TimeSpan(14, 37, 42), 15));
        Assert.Equal(new TimeSpan(23, 0, 0), TimeMath.SnapMinutes(new TimeSpan(23, 59, 0), 60));
        Assert.Equal(new TimeSpan(0, 45, 0), TimeMath.SnapMinutes(new TimeSpan(24, 50, 0), 15));
    }

    [Fact]
    public void Times_wrap_into_a_single_day()
    {
        Assert.Equal(new TimeSpan(1, 0, 0), TimeMath.Normalize(TimeSpan.FromHours(25)));
        Assert.Equal(new TimeSpan(23, 0, 0), TimeMath.Normalize(TimeSpan.FromHours(-1)));
        Assert.Equal(new TimeSpan(0, 0, 0), TimeMath.Normalize(TimeSpan.Zero));
    }

    [Fact]
    public void Add_minutes_and_seconds_wrap()
    {
        Assert.Equal(new TimeSpan(0, 10, 0), TimeMath.AddMinutes(new TimeSpan(23, 50, 0), 20));
        Assert.Equal(new TimeSpan(23, 59, 50), TimeMath.AddSeconds(new TimeSpan(0, 0, 10), -20));
    }

    [Fact]
    public void Compose_wraps_out_of_range_components()
    {
        Assert.Equal(new TimeSpan(1, 0, 0), TimeMath.Compose(25, 0, 0));
        Assert.Equal(new TimeSpan(14, 30, 45), TimeMath.Compose(14, 30, 45));
    }

    [Fact]
    public void Split_returns_the_components_of_a_normalized_time()
    {
        TimeMath.Split(new TimeSpan(25, 2, 3), out int hour, out int minute, out int second);

        Assert.Equal(1, hour);
        Assert.Equal(2, minute);
        Assert.Equal(3, second);
    }

    [Fact]
    public void Formatting_honours_the_clock_and_seconds()
    {
        var time = new TimeSpan(14, 5, 9);
        var invariant = CultureInfo.InvariantCulture;

        Assert.Equal("14:05", TimeMath.Format(time, invariant, ClockFormat.TwentyFourHour, false));
        Assert.Equal("14:05:09", TimeMath.Format(time, invariant, ClockFormat.TwentyFourHour, true));
        Assert.Equal("2:05 PM", TimeMath.Format(time, invariant, ClockFormat.TwelveHour, false));
        Assert.Equal("2:05:09 PM", TimeMath.Format(time, invariant, ClockFormat.TwelveHour, true));
        Assert.Equal("14-05", TimeMath.Format(time, invariant, ClockFormat.TwentyFourHour, false, "HH-mm"));
    }

    [Fact]
    public void Parsing_handles_both_clock_styles()
    {
        Assert.True(TimeMath.TryParse("14:30", CultureInfo.InvariantCulture, ClockFormat.TwentyFourHour, false, out var day));
        Assert.Equal(new TimeSpan(14, 30, 0), day);

        Assert.True(TimeMath.TryParse("2:30 PM", CultureInfo.InvariantCulture, ClockFormat.TwelveHour, false, out var afternoon));
        Assert.Equal(new TimeSpan(14, 30, 0), afternoon);

        Assert.True(TimeMath.TryParse("09:15:42", CultureInfo.InvariantCulture, ClockFormat.TwentyFourHour, true, out var withSeconds));
        Assert.Equal(new TimeSpan(9, 15, 42), withSeconds);
    }

    [Fact]
    public void Invalid_time_text_returns_false_without_throwing()
    {
        Assert.False(TimeMath.TryParse("", CultureInfo.InvariantCulture, ClockFormat.TwentyFourHour, false, out _));
        Assert.False(TimeMath.TryParse(null, CultureInfo.InvariantCulture, ClockFormat.TwentyFourHour, false, out _));
        Assert.False(TimeMath.TryParse("quarter past noon", CultureInfo.InvariantCulture, ClockFormat.TwentyFourHour, false, out _));
    }
}
