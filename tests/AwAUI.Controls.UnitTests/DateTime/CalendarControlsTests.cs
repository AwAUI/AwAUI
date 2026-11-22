using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using Xunit;

namespace AwAUI.Controls.UnitTests.DateTimePrimitives;

/// <summary>Calendar, CalendarDatePicker, DatePicker, and TimePicker end-to-end behavior.</summary>
public class CalendarControlsTests
{
    static Element Day(UIHarness h, string iso) =>
        h.FindAll(e => e.GetAttribute("part") == "day" && e.GetAttribute("data-date") == iso).Single();

    static Element Option(UIHarness h, string column, string value) =>
        h.FindAll(e => e.GetAttribute("part") == "option"
            && e.GetAttribute("data-column") == column
            && e.GetAttribute("data-value") == value).Single();

    static InputElement Input(UIHarness h) =>
        Assert.IsType<InputElement>(h.FindAll(e => e.GetAttribute("part") == "input").Single());

    [Fact]
    public void Calendar_renders_a_fixed_six_by_seven_grid()
    {
        var h = new UIHarness(() => new Calendar
        {
            DisplayDate = new DateTime(2024, 2, 1),
            FirstDayOfWeek = DayOfWeek.Monday,
        });

        var days = h.FindAll(e => e.GetAttribute("part") == "day");
        Assert.Equal(42, days.Count);
        Assert.Equal("2024-01-29", days[0].GetAttribute("data-date"));
        Assert.Equal("2024-02-29", days[31].GetAttribute("data-date"));
        Assert.Equal("2024-03-10", days[41].GetAttribute("data-date"));
        Assert.Equal("gridcell", days[0].GetAttribute("role"));
    }

    [Fact]
    public void Today_is_marked_only_when_highlighting_is_enabled()
    {
        var h = new UIHarness(() => new Calendar { DisplayDate = DateTime.Today });
        Assert.Single(h.FindAll(e => e.GetAttribute("data-today") is not null));

        var plain = new UIHarness(() => new Calendar { DisplayDate = DateTime.Today, IsTodayHighlighted = false });
        Assert.Empty(plain.FindAll(e => e.GetAttribute("data-today") is not null));
    }

    [Fact]
    public void Single_selection_updates_the_grid_and_reports_the_date()
    {
        DateTime? reported = null;
        var h = new UIHarness(() => new Calendar
        {
            DisplayDate = new DateTime(2024, 2, 1),
            DefaultSelectedDate = new DateTime(2024, 2, 10),
            SelectedDateChanged = date => reported = date,
        });

        h.Click(Day(h, "2024-02-15"));

        Assert.Equal(new DateTime(2024, 2, 15), reported);
        var selected = h.FindAll(e => e.GetAttribute("aria-selected") == "true" && e.GetAttribute("part") == "day").Single();
        Assert.Equal("2024-02-15", selected.GetAttribute("data-date"));
    }

    [Fact]
    public void Multiple_selection_toggles_dates()
    {
        IReadOnlyList<DateTime>? reported = null;
        var h = new UIHarness(() => new Calendar
        {
            SelectionMode = CalendarSelectionMode.Multiple,
            DisplayDate = new DateTime(2024, 2, 1),
            SelectedDatesChanged = dates => reported = dates,
        });

        h.Click(Day(h, "2024-02-05"));
        h.Click(Day(h, "2024-02-07"));
        Assert.Equal(new[] { new DateTime(2024, 2, 5), new DateTime(2024, 2, 7) }, reported);

        h.Click(Day(h, "2024-02-05"));
        Assert.Equal(new[] { new DateTime(2024, 2, 7) }, reported);
        Assert.Single(h.FindAll(e => e.GetAttribute("part") == "day" && e.GetAttribute("aria-selected") == "true"));
    }

    [Fact]
    public void Range_selection_uses_two_ordered_endpoints()
    {
        CalendarRange? reported = null;
        var h = new UIHarness(() => new Calendar
        {
            SelectionMode = CalendarSelectionMode.Range,
            DisplayDate = new DateTime(2024, 2, 1),
            RangeChanged = range => reported = range,
        });

        h.Click(Day(h, "2024-02-12"));
        Assert.True(reported.HasValue);
        Assert.Equal(new DateTime(2024, 2, 12), reported!.Value.Start);
        Assert.Null(reported.Value.End);

        h.Click(Day(h, "2024-02-04"));
        Assert.Equal(new DateTime(2024, 2, 4), reported!.Value.Start);
        Assert.Equal(new DateTime(2024, 2, 12), reported.Value.End);

        var selected = h.FindAll(e => e.GetAttribute("part") == "day" && e.GetAttribute("aria-selected") == "true");
        Assert.Equal(9, selected.Count);
    }

    [Fact]
    public void Keyboard_moves_the_roving_tab_stop_and_enter_selects()
    {
        DateTime? reported = null;
        var h = new UIHarness(() => new Calendar
        {
            DisplayDate = new DateTime(2024, 2, 1),
            SelectedDateChanged = date => reported = date,
        });

        Day(h, "2024-02-10").Focus();
        h.Frame();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal("2024-02-11", h.Focused?.GetAttribute("data-date"));

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(new DateTime(2024, 2, 11), reported);
    }

    [Fact]
    public void Page_down_moves_to_the_next_month()
    {
        var h = new UIHarness(() => new Calendar
        {
            DefaultDisplayDate = new DateTime(2024, 2, 1),
            FirstDayOfWeek = DayOfWeek.Monday,
        });

        Day(h, "2024-02-28").Focus();
        h.Frame();
        h.Key(PhysicalKey.PageDown, "PageDown");

        Assert.Equal("2024-03-28", h.Focused?.GetAttribute("data-date"));
    }

    [Fact]
    public void Dates_outside_the_bounds_are_disabled_and_cannot_be_selected()
    {
        DateTime? reported = null;
        var h = new UIHarness(() => new Calendar
        {
            DisplayDate = new DateTime(2024, 2, 1),
            DisplayDateStart = new DateTime(2024, 2, 10),
            SelectedDateChanged = date => reported = date,
        });

        var before = Day(h, "2024-02-09");
        Assert.Equal("true", before.GetAttribute("aria-disabled"));
        h.Click(before);
        Assert.Null(reported);

        h.Click(Day(h, "2024-02-10"));
        Assert.Equal(new DateTime(2024, 2, 10), reported);
    }

    [Fact]
    public void Navigation_buttons_change_the_display_month()
    {
        DateTime reported = default;
        var h = new UIHarness(() => new Calendar
        {
            DisplayDate = new DateTime(2024, 1, 15),
            DisplayDateChanged = date => reported = date,
        });

        h.Click(h.FindAll(e => e.GetAttribute("part") == "next-month").Single());
        Assert.Equal(new DateTime(2024, 2, 1), reported);
    }

    [Fact]
    public void Calendar_date_picker_formats_and_parses_with_the_culture()
    {
        DateTime? selected = null;
        var h = new UIHarness(() => new CalendarDatePicker
        {
            Culture = CultureInfo.InvariantCulture,
            DefaultSelectedDate = new DateTime(2026, 3, 15),
            SelectedDateChanged = date => selected = date,
        });

        Assert.Equal("03/15/2026", Input(h).Value);

        var input = Input(h);
        input.Focus();
        input.SetSelectionRange(0, input.Value.Length);
        h.TypeText("04/20/2026");
        h.Key(PhysicalKey.Enter, "Enter");

        Assert.Equal(new DateTime(2026, 4, 20), selected);
        Assert.Equal("04/20/2026", Input(h).Value);
    }

    [Fact]
    public void Calendar_date_picker_rejects_invalid_text_without_crashing()
    {
        DateTime? selected = null;
        var h = new UIHarness(() => new CalendarDatePicker
        {
            Culture = CultureInfo.InvariantCulture,
            DefaultSelectedDate = new DateTime(2026, 3, 15),
            SelectedDateChanged = date => selected = date,
        });

        var input = Input(h);
        input.Focus();
        input.SetSelectionRange(0, input.Value.Length);
        h.TypeText("not a date");
        Assert.Equal("true", Input(h).GetAttribute("aria-invalid"));

        h.Key(PhysicalKey.Enter, "Enter");

        Assert.Null(selected);
        Assert.Equal("03/15/2026", Input(h).Value);
        Assert.Null(Input(h).GetAttribute("aria-invalid"));
    }

    [Fact]
    public void Calendar_date_picker_uses_a_custom_format()
    {
        var h = new UIHarness(() => new CalendarDatePicker
        {
            Culture = CultureInfo.InvariantCulture,
            DateFormat = CalendarDatePickerFormat.Custom,
            CustomDateFormatString = "yyyy-MM-dd",
            DefaultSelectedDate = new DateTime(2026, 3, 15),
        });

        Assert.Equal("2026-03-15", Input(h).Value);
    }

    [Fact]
    public void Calendar_date_picker_format_property_overrides_the_enum()
    {
        var h = new UIHarness(() => new CalendarDatePicker
        {
            Culture = CultureInfo.InvariantCulture,
            Format = "dd.MM.yyyy",
            DefaultSelectedDate = new DateTime(2026, 3, 5),
        });

        Assert.Equal("05.03.2026", Input(h).Value);
    }

    [Fact]
    public void Calendar_date_picker_dropdown_selects_a_day_and_closes()
    {
        DateTime? selected = null;
        var h = new UIHarness(() => new CalendarDatePicker
        {
            Culture = CultureInfo.InvariantCulture,
            DefaultSelectedDate = new DateTime(2026, 3, 15),
            SelectedDateChanged = date => selected = date,
        });

        h.Click(h.FindAll(e => e.GetAttribute("part") == "button").Single());
        var toggle = h.FindAll(e => e.GetAttribute("part") == "button").Single();
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));

        var day = h.FindAll(e => e.GetAttribute("part") == "day" && e.GetAttribute("data-date") == "2026-03-20");
        Assert.Single(day);
        h.Click(day[0]);

        Assert.Equal(new DateTime(2026, 3, 20), selected);
        Assert.Equal("03/20/2026", Input(h).Value);
        Assert.Equal("false", h.FindAll(e => e.GetAttribute("part") == "button").Single().GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Date_picker_columns_select_year_month_and_day()
    {
        DateTime? selected = null;
        var h = new UIHarness(() => new DatePicker
        {
            DefaultSelectedDate = new DateTime(2024, 6, 15),
            MinYear = 2020,
            MaxYear = 2025,
            SelectedDateChanged = date => selected = date,
        });

        Assert.Equal(6, h.FindAll(e => e.GetAttribute("data-column") == "year" && e.GetAttribute("part") == "option").Count);
        Assert.Equal("true", Option(h, "year", "2024").GetAttribute("aria-selected"));
        Assert.Equal("listbox", h.FindAll(e => e.GetAttribute("part") == "year" && e.GetAttribute("role") == "listbox").Single().GetAttribute("role"));

        h.Click(Option(h, "year", "2025"));
        Assert.Equal(new DateTime(2025, 6, 15), selected);

        h.Click(Option(h, "month", "2"));
        Assert.Equal(new DateTime(2025, 2, 15), selected);
    }

    [Fact]
    public void Date_picker_clamps_the_day_when_the_month_is_shorter()
    {
        DateTime? selected = null;
        var h = new UIHarness(() => new DatePicker
        {
            DefaultSelectedDate = new DateTime(2024, 1, 31),
            MinYear = 2024,
            MaxYear = 2024,
            SelectedDateChanged = date => selected = date,
        });

        h.Click(Option(h, "month", "2"));
        Assert.Equal(new DateTime(2024, 2, 29), selected);
    }

    [Fact]
    public void Date_picker_can_hide_columns()
    {
        var h = new UIHarness(() => new DatePicker
        {
            DefaultSelectedDate = new DateTime(2024, 6, 15),
            DayVisible = false,
            MonthVisible = false,
        });

        Assert.Empty(h.FindAll(e => e.GetAttribute("part") == "day"));
        Assert.Empty(h.FindAll(e => e.GetAttribute("part") == "month"));
        Assert.NotEmpty(h.FindAll(e => e.GetAttribute("part") == "year" && e.GetAttribute("role") == "listbox"));
    }

    [Fact]
    public void Date_picker_arrow_keys_move_the_selection_and_focus()
    {
        DateTime? selected = null;
        var h = new UIHarness(() => new DatePicker
        {
            DefaultSelectedDate = new DateTime(2024, 6, 15),
            MinYear = 2020,
            MaxYear = 2025,
            SelectedDateChanged = date => selected = date,
        });

        Option(h, "year", "2024").Focus();
        h.Frame();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        Assert.Equal(new DateTime(2025, 6, 15), selected);
        Assert.Equal("2025", h.Focused?.GetAttribute("data-value"));
    }

    [Fact]
    public void Time_picker_selects_hour_and_minute_in_24_hour_mode()
    {
        TimeSpan? selected = null;
        var h = new UIHarness(() => new TimePicker
        {
            DefaultSelectedTime = new TimeSpan(14, 30, 0),
            SelectedTimeChanged = time => selected = time,
        });

        Assert.Equal("true", Option(h, "hour", "14").GetAttribute("aria-selected"));
        Assert.Equal("true", Option(h, "minute", "30").GetAttribute("aria-selected"));
        Assert.Equal(24, h.FindAll(e => e.GetAttribute("data-column") == "hour" && e.GetAttribute("part") == "option").Count);

        h.Click(Option(h, "hour", "0"));
        Assert.Equal(new TimeSpan(0, 30, 0), selected);

        h.Click(Option(h, "minute", "5"));
        Assert.Equal(new TimeSpan(0, 5, 0), selected);
    }

    [Fact]
    public void Time_picker_uses_an_am_pm_column_for_the_twelve_hour_clock()
    {
        TimeSpan? selected = null;
        var h = new UIHarness(() => new TimePicker
        {
            ClockIdentifier = ClockIdentifiers.TwelveHourClock,
            DefaultSelectedTime = new TimeSpan(14, 30, 0),
            SelectedTimeChanged = time => selected = time,
        });

        Assert.Equal("true", Option(h, "hour", "2").GetAttribute("aria-selected"));
        Assert.Equal("true", Option(h, "period", "pm").GetAttribute("aria-selected"));

        h.Click(Option(h, "period", "am"));
        Assert.Equal(new TimeSpan(2, 30, 0), selected);

        h.Click(Option(h, "hour", "3"));
        Assert.Equal(new TimeSpan(3, 30, 0), selected);
    }

    [Fact]
    public void Time_picker_minute_increment_limits_the_offered_values()
    {
        var h = new UIHarness(() => new TimePicker
        {
            DefaultSelectedTime = new TimeSpan(9, 30, 0),
            MinuteIncrement = 15,
            ShowSeconds = true,
        });

        var minutes = h.FindAll(e => e.GetAttribute("data-column") == "minute" && e.GetAttribute("part") == "option")
            .Select(e => e.GetAttribute("data-value")).ToArray();
        Assert.Equal(new[] { "0", "15", "30", "45" }, minutes);
        Assert.NotEmpty(h.FindAll(e => e.GetAttribute("data-column") == "second" && e.GetAttribute("part") == "option"));
    }

    [Fact]
    public void Time_picker_arrow_keys_change_the_time()
    {
        TimeSpan? selected = null;
        var h = new UIHarness(() => new TimePicker
        {
            DefaultSelectedTime = new TimeSpan(14, 30, 0),
            SelectedTimeChanged = time => selected = time,
        });

        Option(h, "hour", "14").Focus();
        h.Frame();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        Assert.Equal(new TimeSpan(15, 30, 0), selected);
        Assert.Equal("15", h.Focused?.GetAttribute("data-value"));
    }
}
