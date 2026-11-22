using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// The six-week, seven-day grid shown for one calendar month. <see cref="Days"/> always has 42 entries, including
/// the trailing days of the previous month and the leading days of the next month.
/// </summary>
public readonly struct CalendarMonthLayout
{
    readonly ReadOnlyCollection<DateTime> _days;

    public CalendarMonthLayout(DateTime monthStart, DateTime gridStart, DayOfWeek firstDayOfWeek, DateTime[] days)
    {
        MonthStart = monthStart.Date;
        GridStart = gridStart.Date;
        FirstDayOfWeek = firstDayOfWeek;
        _days = new ReadOnlyCollection<DateTime>(days ?? throw new ArgumentNullException(nameof(days)));
    }

    /// <summary>The first day of the displayed month.</summary>
    public DateTime MonthStart { get; }

    /// <summary>The first cell of the grid (a day in the previous month, or the first day itself).</summary>
    public DateTime GridStart { get; }

    /// <summary>The day of the week represented by the first column.</summary>
    public DayOfWeek FirstDayOfWeek { get; }

    /// <summary>The 42 grid cells in reading order.</summary>
    public IReadOnlyList<DateTime> Days => _days;

    /// <summary>Always six for a calendar month.</summary>
    public int WeekCount => 6;

    /// <summary>Whether <paramref name="date"/> belongs to the displayed month.</summary>
    public bool IsInMonth(DateTime date) => date.Year == MonthStart.Year && date.Month == MonthStart.Month;
}

/// <summary>
/// Pure month-grid, navigation, and clamping helpers shared by the date controls. Everything here is independent of
/// the UI and can be tested in isolation.
/// </summary>
public static class CalendarMath
{
    /// <summary>Number of weeks rendered for every month.</summary>
    public const int WeeksPerMonth = 6;

    /// <summary>Number of cells in a month grid (six rows of seven days).</summary>
    public const int DaysPerMonthGrid = WeeksPerMonth * 7;

    /// <summary>Returns the first day of the month containing <paramref name="value"/>.</summary>
    public static DateTime MonthStart(DateTime value) => new DateTime(value.Year, value.Month, 1);

    /// <summary>Resolves the configured first day of the week, falling back to the culture's setting.</summary>
    public static DayOfWeek ResolveFirstDayOfWeek(CultureInfo? culture, DayOfWeek? configured) =>
        configured ?? (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.FirstDayOfWeek;

    /// <summary>Number of cells before the first day of <paramref name="month"/> in a week starting on <paramref name="firstDayOfWeek"/>.</summary>
    public static int LeadingDayCount(DateTime month, DayOfWeek firstDayOfWeek)
    {
        var start = MonthStart(month);
        return ((int)start.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
    }

    /// <summary>Builds the fixed six-by-seven grid for <paramref name="month"/>.</summary>
    public static CalendarMonthLayout LayoutMonth(DateTime month, DayOfWeek firstDayOfWeek)
    {
        var start = MonthStart(month);
        var gridStart = start.AddDays(-LeadingDayCount(start, firstDayOfWeek));
        var days = new DateTime[DaysPerMonthGrid];
        for (int i = 0; i < days.Length; i++) days[i] = gridStart.AddDays(i);
        return new CalendarMonthLayout(start, gridStart, firstDayOfWeek, days);
    }

    /// <summary>Returns the first day of the week containing <paramref name="value"/>.</summary>
    public static DateTime StartOfWeek(DateTime value, DayOfWeek firstDayOfWeek)
    {
        int offset = ((int)value.Date.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        try
        {
            return value.Date.AddDays(-offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue.Date;
        }
    }

    /// <summary>ISO week-of-year using the culture's week rule and the configured first day.</summary>
    public static int WeekOfYear(DateTime value, CultureInfo culture, DayOfWeek firstDayOfWeek)
    {
        if (culture is null) throw new ArgumentNullException(nameof(culture));
        var calendar = culture.Calendar;
        return calendar.GetWeekOfYear(value.Date, culture.DateTimeFormat.CalendarWeekRule, firstDayOfWeek);
    }

    /// <summary>Clamps <paramref name="value"/> into the inclusive range; either bound may be null.</summary>
    public static DateTime ClampDate(DateTime value, DateTime? min, DateTime? max)
    {
        var day = value.Date;
        if (min is { } lower && day < lower.Date) day = lower.Date;
        if (max is { } upper && day > upper.Date) day = upper.Date;
        return day;
    }

    /// <summary>Clamps a month (normalized to its first day) so it does not fall outside the bound months.</summary>
    public static DateTime ClampMonth(DateTime month, DateTime? min, DateTime? max)
    {
        var result = MonthStart(month);
        if (min is { } lower)
        {
            var lowerMonth = MonthStart(lower);
            if (result < lowerMonth) result = lowerMonth;
        }
        if (max is { } upper)
        {
            var upperMonth = MonthStart(upper);
            if (result > upperMonth) result = upperMonth;
        }
        return result;
    }

    /// <summary>Whether <paramref name="value"/> falls inside the inclusive bounds.</summary>
    public static bool IsWithin(DateTime value, DateTime? min, DateTime? max)
    {
        var day = value.Date;
        if (min is { } lower && day < lower.Date) return false;
        if (max is { } upper && day > upper.Date) return false;
        return true;
    }

    /// <summary>Adds months while keeping the day of month inside the target month.</summary>
    public static DateTime AddMonthsClamped(DateTime value, int months)
    {
        DateTime target;
        try
        {
            target = value.AddMonths(months);
        }
        catch (ArgumentOutOfRangeException)
        {
            return months >= 0 ? DateTime.MaxValue : DateTime.MinValue;
        }
        int day = Math.Min(value.Day, DateTime.DaysInMonth(target.Year, target.Month));
        return new DateTime(target.Year, target.Month, day, value.Hour, value.Minute, value.Second, value.Millisecond, value.Kind);
    }

    /// <summary>Adds days without overflowing the representable date range.</summary>
    public static DateTime AddDaysClamped(DateTime value, int days)
    {
        try
        {
            return value.AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            return days >= 0 ? DateTime.MaxValue : DateTime.MinValue;
        }
    }

    /// <summary>Adds years while keeping February 29 valid in non-leap years.</summary>
    public static DateTime AddYearsClamped(DateTime value, int years) => AddMonthsClamped(value, years * 12);

    /// <summary>Clamps a year into the inclusive year bounds.</summary>
    public static int ClampYear(int year, int minYear, int maxYear)
    {
        int lower = Math.Max(1, Math.Min(minYear, maxYear));
        int upper = Math.Min(9999, Math.Max(minYear, maxYear));
        if (year < lower) return lower;
        if (year > upper) return upper;
        return year;
    }

    /// <summary>
    /// Returns the day number to keep when moving to another month: the original day, or the last day of the target
    /// month when it is shorter (for example 31 → 30).
    /// </summary>
    public static int ClampDay(int day, int year, int month) =>
        Math.Max(1, Math.Min(day, DateTime.DaysInMonth(year, month)));
}
