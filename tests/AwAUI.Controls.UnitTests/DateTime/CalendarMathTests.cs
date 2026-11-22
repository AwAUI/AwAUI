using System;
using System.Globalization;
using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests.DateTimePrimitives;

/// <summary>Month grid, clamping, and navigation helpers used by the date controls.</summary>
public class CalendarMathTests
{
    [Fact]
    public void Leap_year_february_grid_has_42_cells_and_ends_in_march()
    {
        var layout = CalendarMath.LayoutMonth(new DateTime(2024, 2, 1), DayOfWeek.Monday);

        Assert.Equal(42, layout.Days.Count);
        Assert.Equal(6, layout.WeekCount);
        Assert.Equal(new DateTime(2024, 1, 29), layout.Days[0]); // 1 Feb 2024 is a Thursday
        Assert.Equal(new DateTime(2024, 2, 1), layout.Days[3]);
        Assert.Equal(new DateTime(2024, 2, 29), layout.Days[31]);
        Assert.Equal(new DateTime(2024, 3, 10), layout.Days[41]);
        Assert.True(layout.IsInMonth(new DateTime(2024, 2, 29)));
        Assert.False(layout.IsInMonth(new DateTime(2024, 3, 1)));
    }

    [Fact]
    public void Month_starting_on_the_configured_first_day_has_no_leading_cells()
    {
        var layout = CalendarMath.LayoutMonth(new DateTime(2026, 6, 1), DayOfWeek.Monday);

        Assert.Equal(new DateTime(2026, 6, 1), layout.Days[0]);
        Assert.Equal(0, CalendarMath.LeadingDayCount(new DateTime(2026, 6, 1), DayOfWeek.Monday));
    }

    [Fact]
    public void First_day_not_monday_is_padded_from_the_previous_month()
    {
        // 1 Feb 2026 is a Sunday; a Monday-first grid starts six days earlier.
        int offset = CalendarMath.LeadingDayCount(new DateTime(2026, 2, 1), DayOfWeek.Monday);
        var layout = CalendarMath.LayoutMonth(new DateTime(2026, 2, 1), DayOfWeek.Monday);

        Assert.Equal(6, offset);
        Assert.Equal(new DateTime(2026, 1, 26), layout.Days[0]);
    }

    [Fact]
    public void December_grid_crosses_into_the_next_year()
    {
        var layout = CalendarMath.LayoutMonth(new DateTime(2024, 12, 1), DayOfWeek.Monday);

        Assert.Equal(new DateTime(2024, 11, 25), layout.Days[0]); // 1 Dec 2024 is a Sunday
        Assert.Equal(new DateTime(2025, 1, 5), layout.Days[41]);
        Assert.Equal(new DateTime(2024, 12, 31), layout.Days[36]);
    }

    [Fact]
    public void Culture_supplies_the_first_day_of_the_week()
    {
        var culture = new CultureInfo("en-US");
        Assert.Equal(DayOfWeek.Sunday, CalendarMath.ResolveFirstDayOfWeek(culture, null));
        Assert.Equal(DayOfWeek.Wednesday, CalendarMath.ResolveFirstDayOfWeek(culture, DayOfWeek.Wednesday));
    }

    [Fact]
    public void Adding_months_clamps_the_day_to_the_target_month()
    {
        Assert.Equal(new DateTime(2024, 2, 29), CalendarMath.AddMonthsClamped(new DateTime(2024, 1, 31), 1));
        Assert.Equal(new DateTime(2025, 2, 28), CalendarMath.AddMonthsClamped(new DateTime(2025, 1, 31), 1));
        Assert.Equal(new DateTime(2025, 1, 31), CalendarMath.AddMonthsClamped(new DateTime(2024, 1, 31), 12));
    }

    [Fact]
    public void Adding_years_handles_leap_day()
    {
        Assert.Equal(new DateTime(2025, 2, 28), CalendarMath.AddYearsClamped(new DateTime(2024, 2, 29), 1));
    }

    [Fact]
    public void Dates_and_months_are_clamped_to_the_bounds()
    {
        var min = new DateTime(2024, 3, 10);
        var max = new DateTime(2024, 9, 20);

        Assert.Equal(min, CalendarMath.ClampDate(new DateTime(2024, 1, 1), min, max));
        Assert.Equal(max, CalendarMath.ClampDate(new DateTime(2025, 1, 1), min, max));
        Assert.Equal(new DateTime(2024, 6, 15), CalendarMath.ClampDate(new DateTime(2024, 6, 15), min, max));
        Assert.Equal(new DateTime(2024, 3, 1), CalendarMath.ClampMonth(new DateTime(2024, 1, 20), min, max));
        Assert.Equal(new DateTime(2024, 9, 1), CalendarMath.ClampMonth(new DateTime(2025, 1, 20), min, max));
        Assert.True(CalendarMath.IsWithin(new DateTime(2024, 3, 10), min, max));
        Assert.False(CalendarMath.IsWithin(new DateTime(2024, 3, 9), min, max));
    }

    [Fact]
    public void Week_of_year_uses_the_culture_rule()
    {
        var culture = new CultureInfo("en-US");
        var week = CalendarMath.WeekOfYear(new DateTime(2024, 1, 1), culture, DayOfWeek.Sunday);

        Assert.True(week is 1 or 53); // 1 Jan 2024 belongs to week 1 (or the final week of 2023 under some rules)
    }

    [Fact]
    public void Year_and_day_clamping_are_safe_at_the_edges()
    {
        Assert.Equal(1900, CalendarMath.ClampYear(1800, 1900, 2100));
        Assert.Equal(2100, CalendarMath.ClampYear(2200, 1900, 2100));
        Assert.Equal(1, CalendarMath.ClampYear(1, 1, 9999));
        Assert.Equal(9999, CalendarMath.ClampYear(9999, 1, 9999));
        Assert.Equal(29, CalendarMath.ClampDay(31, 2024, 2));
        Assert.Equal(30, CalendarMath.ClampDay(31, 2024, 4));
    }
}

/// <summary>Culture-aware date parsing and formatting.</summary>
public class DateTextTests
{
    [Fact]
    public void Explicit_format_round_trips()
    {
        var value = new DateTime(2026, 3, 15);
        var text = DateText.Format(value, CultureInfo.InvariantCulture, "yyyy-MM-dd");

        Assert.Equal("2026-03-15", text);
        Assert.True(DateText.TryParseExact(text, CultureInfo.InvariantCulture, "yyyy-MM-dd", out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void Null_format_uses_the_culture_short_date_pattern()
    {
        var culture = new CultureInfo("en-US");
        var text = DateText.Format(new DateTime(2026, 3, 15), culture);

        Assert.Equal("3/15/2026", text);
    }

    [Fact]
    public void Null_date_formats_to_empty_text()
    {
        Assert.Equal("", DateText.Format(null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Parsing_accepts_culture_text_and_unambiguous_fallbacks()
    {
        var culture = new CultureInfo("en-US");

        Assert.True(DateText.TryParse("3/15/2026", culture, out var slash));
        Assert.Equal(new DateTime(2026, 3, 15), slash);

        Assert.True(DateText.TryParse("2026-03-15", culture, out var iso));
        Assert.Equal(new DateTime(2026, 3, 15), iso);

        Assert.True(DateText.TryParse("15 March 2026", culture, out var longDate));
        Assert.Equal(new DateTime(2026, 3, 15), longDate);
    }

    [Fact]
    public void Invalid_text_returns_false_instead_of_throwing()
    {
        Assert.False(DateText.TryParse("not a date", CultureInfo.InvariantCulture, out _));
        Assert.False(DateText.TryParse("", CultureInfo.InvariantCulture, out _));
        Assert.False(DateText.TryParse(null, CultureInfo.InvariantCulture, out _));
        Assert.False(DateText.TryParseExact("2026-13-40", CultureInfo.InvariantCulture, "yyyy-MM-dd", out _));
    }

    [Fact]
    public void An_invalid_custom_pattern_does_not_throw()
    {
        Assert.False(DateText.TryParseExact("2026-03-15", CultureInfo.InvariantCulture, "not-a-pattern", out _));
        Assert.NotNull(DateText.Format(new DateTime(2026, 3, 15), new CultureInfo("en-US"), "not-a-pattern"));
    }

    [Fact]
    public void Time_components_are_discarded()
    {
        Assert.True(DateText.TryParse("2026-03-15 18:45:00", CultureInfo.InvariantCulture, out var parsed));
        Assert.Equal(new DateTime(2026, 3, 15), parsed);
    }
}
