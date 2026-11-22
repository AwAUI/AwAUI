using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// Shadcn S2 <see cref="Calendar"/> acceptance: localized month/weekday grid, ARIA roles and per-day state, single-date
/// selection (controlled and uncontrolled), min/max and predicate disabling, outside days, week numbers, and the complete
/// keyboard model (arrows, Home/End, PageUp/PageDown, Enter/Space) with a roving tab stop.
/// </summary>
public class CalendarTests
{
    static readonly DateTime January2026 = new(2026, 1, 1);

    static CalendarTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element? Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault();

    static Element Day(UIHarness h, string day) =>
        h.FindAll(e => A(e, "data-slot") == "calendar-day" && A(e, "data-day") == day).Single();

    static Element TabStop(UIHarness h) =>
        h.FindAll(e => A(e, "data-slot") == "calendar-day" && e.TabIndex == 0).Single();

    static string? FocusedDay(UIHarness h) => h.Focused is { } focused ? A(focused, "data-day") : null;

    static void FocusTabStop(UIHarness h) => TabStop(h).Focus();

    // ------------------------------------------------------------ structure and semantics

    [Fact]
    public void Calendar_renders_a_localized_grid_with_roles_and_day_state()
    {
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("en-US"),
        });

        var root = Slot(h, "calendar");
        Assert.NotNull(root);

        var grid = h.FindAll(e => e.Role == Role.Grid).Single();
        Assert.Equal("calendar-grid", A(grid, "data-slot"));

        var weekdayRow = h.FindAll(e => A(e, "data-slot") == "calendar-weekdays").Single();
        Assert.Equal(Role.Row, weekdayRow.Role);
        var weekdays = h.FindAll(e => A(e, "data-slot") == "calendar-weekday");
        Assert.Equal(7, weekdays.Count);
        Assert.Equal("Su", weekdays[0].TextContent);
        Assert.Equal("Sa", weekdays[6].TextContent);

        // January 2026 starts on Thursday, so the grid begins on December 28 and shows 35 cells.
        var days = h.FindAll(e => A(e, "data-slot") == "calendar-day");
        Assert.Equal(35, days.Count);
        Assert.All(days, day => Assert.Equal(Role.Cell, day.Role));

        var outside = Day(h, "12/28/2025");
        Assert.Equal("", A(outside, "data-outside"));

        var first = Day(h, "1/1/2026");
        Assert.Null(A(first, "data-outside"));
        Assert.Equal("false", A(first, "aria-selected"));
        Assert.Equal("false", A(first, "aria-disabled"));
        Assert.Contains("January 1, 2026", A(first, "aria-label"));

        // Exactly one day is in the tab order (the roving tab stop).
        Assert.Single(days, day => day.TabIndex == 0);
    }

    [Fact]
    public void Calendar_marks_today_and_applies_a_selected_outside_day()
    {
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = DateTime.Today,
            DefaultSelectedDate = DateTime.Today.AddDays(-1),
            Culture = new CultureInfo("en-US"),
        });

        var today = h.FindAll(e => A(e, "data-today") == "").Single();
        Assert.Equal("date", A(today, "aria-current"));

        var selected = h.FindAll(e => A(e, "data-selected") == "true").Single();
        Assert.Equal("true", A(selected, "aria-selected"));
        Assert.Contains("bg-primary", selected.ClassName);
    }

    // ------------------------------------------------------------ selection

    [Fact]
    public void Calendar_selects_with_pointer_and_reports_the_uncontrolled_value()
    {
        DateTime? selected = null;
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("en-US"),
            SelectedDateChanged = value => selected = value,
        });

        var day = Day(h, "1/15/2026");
        Assert.Equal("false", A(day, "aria-selected"));

        h.Click(day);

        Assert.Equal(new DateTime(2026, 1, 15), selected);
        Assert.Equal("true", A(Day(h, "1/15/2026"), "aria-selected"));
        Assert.Equal("true", A(Day(h, "1/15/2026"), "data-selected"));
        Assert.Equal("false", A(Day(h, "1/14/2026"), "aria-selected"));
    }

    [Fact]
    public void Calendar_controlled_selection_reports_changes_without_switching()
    {
        DateTime? reported = null;
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            SelectedDate = new DateTime(2026, 1, 10),
            IsSelectedDateControlled = true,
            Culture = new CultureInfo("en-US"),
            SelectedDateChanged = value => reported = value,
        });

        Assert.Equal("true", A(Day(h, "1/10/2026"), "aria-selected"));

        h.Click(Day(h, "1/12/2026"));

        Assert.Equal(new DateTime(2026, 1, 12), reported);
        // The parent did not update the property, so the controlled value is unchanged.
        Assert.Equal("true", A(Day(h, "1/10/2026"), "aria-selected"));
        Assert.Equal("false", A(Day(h, "1/12/2026"), "aria-selected"));
    }

    // ------------------------------------------------------------ keyboard

    [Fact]
    public void Calendar_arrow_keys_move_the_roving_focus_by_day_and_week()
    {
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("en-US"),
        });

        FocusTabStop(h);
        Assert.Equal("1/1/2026", FocusedDay(h));

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal("1/2/2026", FocusedDay(h));
        Assert.Equal(0, h.Focused!.TabIndex);

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("1/9/2026", FocusedDay(h));

        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        Assert.Equal("1/8/2026", FocusedDay(h));

        h.Key(PhysicalKey.ArrowUp, "ArrowUp");
        Assert.Equal("1/1/2026", FocusedDay(h));

        // Only the focused day stays in the tab order.
        Assert.Single(h.FindAll(e => A(e, "data-slot") == "calendar-day"), day => day.TabIndex == 0);
    }

    [Fact]
    public void Calendar_home_and_end_move_to_the_week_edges()
    {
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("en-US"),
        });

        Day(h, "1/15/2026").Focus();
        Assert.Equal("1/15/2026", FocusedDay(h));

        h.Key(PhysicalKey.Home, "Home");
        Assert.Equal("1/11/2026", FocusedDay(h));

        h.Key(PhysicalKey.End, "End");
        Assert.Equal("1/17/2026", FocusedDay(h));
    }

    [Fact]
    public void Calendar_page_keys_move_by_month_and_shift_page_by_year()
    {
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("en-US"),
        });

        Day(h, "1/15/2026").Focus();

        h.Key(PhysicalKey.PageDown, "PageDown");
        Assert.Equal("2/15/2026", FocusedDay(h));
        Assert.Equal("February 2026", Slot(h, "calendar-caption")!.TextContent);

        h.Key(PhysicalKey.PageUp, "PageUp");
        Assert.Equal("1/15/2026", FocusedDay(h));
        Assert.Equal("January 2026", Slot(h, "calendar-caption")!.TextContent);

        h.Key(PhysicalKey.PageDown, "PageDown", KeyModifiers.Shift);
        Assert.Equal("1/15/2027", FocusedDay(h));
        Assert.Equal("January 2027", Slot(h, "calendar-caption")!.TextContent);

        h.Key(PhysicalKey.PageUp, "PageUp", KeyModifiers.Shift);
        Assert.Equal("1/15/2026", FocusedDay(h));
    }

    [Fact]
    public void Calendar_enter_and_space_select_the_focused_day()
    {
        DateTime? selected = null;
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("en-US"),
            SelectedDateChanged = value => selected = value,
        });

        Day(h, "1/15/2026").Focus();
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(new DateTime(2026, 1, 15), selected);

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        h.Key(PhysicalKey.Space, " ");
        Assert.Equal(new DateTime(2026, 1, 16), selected);
    }

    // ------------------------------------------------------------ disabled dates

    [Fact]
    public void Calendar_honors_min_max_and_the_disabled_predicate()
    {
        DateTime? selected = null;
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("en-US"),
            MinDate = new DateTime(2026, 1, 10),
            MaxDate = new DateTime(2026, 1, 20),
            DisabledDates = date => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            SelectedDateChanged = value => selected = value,
        });

        Assert.Equal("true", A(Day(h, "1/10/2026"), "aria-disabled")); // Saturday
        Assert.Equal("true", A(Day(h, "1/5/2026"), "aria-disabled"));  // before the minimum
        Assert.Equal("true", A(Day(h, "1/25/2026"), "aria-disabled")); // after the maximum
        Assert.Equal("false", A(Day(h, "1/12/2026"), "aria-disabled"));

        // The roving tab stop starts on the first enabled day.
        Assert.Equal("1/12/2026", A(TabStop(h), "data-day"));

        h.Click(Day(h, "1/10/2026"));
        Assert.Null(selected);

        h.Click(Day(h, "1/12/2026"));
        Assert.Equal(new DateTime(2026, 1, 12), selected);

        // Arrow navigation skips disabled days and stops at the range boundary.
        Day(h, "1/12/2026").Focus();
        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        Assert.Equal("1/12/2026", FocusedDay(h));
    }

    // ------------------------------------------------------------ outside days and cultures

    [Fact]
    public void Calendar_outside_days_select_and_navigate_the_month()
    {
        DateTime? selected = null;
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("en-US"),
            SelectedDateChanged = value => selected = value,
        });

        h.Click(Day(h, "12/30/2025"));

        Assert.Equal(new DateTime(2025, 12, 30), selected);
        Assert.Equal("December 2025", Slot(h, "calendar-caption")!.TextContent);
        Assert.Equal("true", A(Day(h, "12/30/2025"), "aria-selected"));
    }

    [Fact]
    public void Calendar_can_hide_outside_days()
    {
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            ShowOutsideDays = false,
            Culture = new CultureInfo("en-US"),
        });

        Assert.Equal(31, h.FindAll(e => A(e, "data-slot") == "calendar-day").Count);
        Assert.Equal(4, h.FindAll(e => A(e, "data-slot") == "calendar-empty").Count);
        Assert.All(h.FindAll(e => A(e, "data-slot") == "calendar-day"), day => Assert.Null(A(day, "data-outside")));
    }

    [Fact]
    public void Calendar_uses_the_culture_for_weekdays_and_labels()
    {
        var culture = new CultureInfo("fr-FR");
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            Culture = culture,
        });

        // French weeks start on Monday.
        var weekdays = h.FindAll(e => A(e, "data-slot") == "calendar-weekday");
        Assert.Equal("lu", weekdays[0].TextContent);
        Assert.Equal("di", weekdays[6].TextContent);
        Assert.Equal("lundi", A(weekdays[0], "aria-label"));

        Assert.Equal("janvier 2026", Slot(h, "calendar-caption")!.TextContent);
        Assert.Contains("15 janvier 2026", A(Day(h, "15/01/2026"), "aria-label"));
    }

    [Fact]
    public void Calendar_can_show_week_numbers()
    {
        var h = Harness(() => new Calendar
        {
            DefaultDisplayDate = January2026,
            ShowWeekNumbers = true,
            Culture = new CultureInfo("en-US"),
        });

        var weekNumbers = h.FindAll(e => A(e, "data-slot") == "calendar-week-number");
        Assert.Equal(5, weekNumbers.Count);
        Assert.All(weekNumbers, cell => Assert.Equal(Role.Cell, cell.Role));
        Assert.All(weekNumbers, cell => Assert.False(string.IsNullOrEmpty(cell.TextContent)));
    }

    // ------------------------------------------------------------ range mode

    [Fact]
    public void Calendar_range_mode_marks_endpoints_and_previews_the_hovered_span()
    {
        DateTime? start = null, end = null;
        var h = Harness(() => new Calendar
        {
            SelectionMode = CalendarSelectionMode.Range,
            DefaultMonth = January2026,
            Culture = new CultureInfo("en-US"),
            RangeChanged = (s, e) => { start = s; end = e; },
        });

        h.Click(Day(h, "1/10/2026"));

        Assert.Equal(new DateTime(2026, 1, 10), start);
        Assert.Null(end);
        Assert.Equal("true", A(Day(h, "1/10/2026"), "data-range-start"));
        Assert.Null(A(Day(h, "1/10/2026"), "data-range-end"));
        Assert.Equal("true", A(Day(h, "1/10/2026"), "data-selected"));
        Assert.Equal("true", A(Day(h, "1/10/2026"), "aria-selected"));
        Assert.Null(A(Day(h, "1/12/2026"), "data-range-middle"));

        // Hovering after the first click previews the span without committing it.
        h.PointerMove(Day(h, "1/15/2026").GetBoundingClientRect().Center);

        Assert.Equal("true", A(Day(h, "1/12/2026"), "data-range-middle"));
        Assert.Equal("true", A(Day(h, "1/12/2026"), "data-range-preview"));
        Assert.Equal("true", A(Day(h, "1/15/2026"), "data-range-end"));
        Assert.Equal("true", A(Day(h, "1/15/2026"), "data-range-preview"));
        Assert.Null(A(Day(h, "1/16/2026"), "data-range-middle"));
        Assert.Null(end);

        h.Click(Day(h, "1/15/2026"));

        Assert.Equal(new DateTime(2026, 1, 10), start);
        Assert.Equal(new DateTime(2026, 1, 15), end);
        Assert.Equal("true", A(Day(h, "1/12/2026"), "data-range-middle"));
        Assert.Null(A(Day(h, "1/12/2026"), "data-range-preview"));
        Assert.Equal("true", A(Day(h, "1/15/2026"), "data-range-end"));
    }

    [Fact]
    public void Calendar_range_mode_swaps_reverse_endpoints_and_accepts_the_same_day()
    {
        DateTime? start = null, end = null;
        var h = Harness(() => new Calendar
        {
            SelectionMode = CalendarSelectionMode.Range,
            DefaultMonth = January2026,
            Culture = new CultureInfo("en-US"),
            RangeChanged = (s, e) => { start = s; end = e; },
        });

        h.Click(Day(h, "1/20/2026"));
        h.Click(Day(h, "1/15/2026"));

        Assert.Equal(new DateTime(2026, 1, 15), start);
        Assert.Equal(new DateTime(2026, 1, 20), end);
        Assert.Equal("true", A(Day(h, "1/15/2026"), "data-range-start"));
        Assert.Equal("true", A(Day(h, "1/20/2026"), "data-range-end"));
        Assert.Equal("true", A(Day(h, "1/17/2026"), "data-range-middle"));

        // A third click starts a new range; clicking the same day twice selects that single day.
        h.Click(Day(h, "1/12/2026"));
        h.Click(Day(h, "1/12/2026"));

        Assert.Equal(new DateTime(2026, 1, 12), start);
        Assert.Equal(new DateTime(2026, 1, 12), end);
        Assert.Equal("true", A(Day(h, "1/12/2026"), "data-range-start"));
        Assert.Equal("true", A(Day(h, "1/12/2026"), "data-range-end"));
        Assert.Null(A(Day(h, "1/12/2026"), "data-range-middle"));
    }

    [Fact]
    public void Calendar_range_mode_honors_the_disabled_predicate_and_from_to_aliases()
    {
        DateTime? start = null, end = null;
        var h = Harness(() => new Calendar
        {
            SelectionMode = CalendarSelectionMode.Range,
            DefaultMonth = January2026,
            Culture = new CultureInfo("en-US"),
            FromDate = new DateTime(2026, 1, 5),
            ToDate = new DateTime(2026, 1, 25),
            IsDateDisabled = date => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            RangeChanged = (s, e) => { start = s; end = e; },
        });

        Assert.Equal("true", A(Day(h, "1/17/2026"), "aria-disabled")); // Saturday
        Assert.Equal("true", A(Day(h, "1/3/2026"), "aria-disabled"));  // before FromDate
        Assert.Equal("true", A(Day(h, "1/26/2026"), "aria-disabled")); // after ToDate
        Assert.Equal("false", A(Day(h, "1/16/2026"), "aria-disabled"));

        h.Click(Day(h, "1/17/2026"));
        Assert.Null(start);
        Assert.Null(end);

        h.Click(Day(h, "1/16/2026"));
        Assert.Equal(new DateTime(2026, 1, 16), start);
        Assert.Null(end);
    }

    [Fact]
    public void Calendar_controlled_month_reports_navigation_and_keeps_the_controlled_month()
    {
        DateTime? reported = null;
        var h = Harness(() => new Calendar
        {
            Month = new DateTime(2026, 1, 1),
            MonthChanged = month => reported = month,
            Culture = new CultureInfo("en-US"),
        });

        Assert.Equal("January 2026", Slot(h, "calendar-caption")!.TextContent);

        h.Click(Slot(h, "calendar-next-month")!);

        Assert.Equal(new DateTime(2026, 2, 1), reported);
        // The parent did not update Month, so the calendar stays on the controlled month.
        Assert.Equal("January 2026", Slot(h, "calendar-caption")!.TextContent);
    }

    // ------------------------------------------------------------ multiple mode

    [Fact]
    public void Calendar_multiple_mode_toggles_dates_and_reports_the_sorted_set()
    {
        IReadOnlyList<DateTime>? selected = null;
        var h = Harness(() => new Calendar
        {
            SelectionMode = CalendarSelectionMode.Multiple,
            DefaultMonth = January2026,
            Culture = new CultureInfo("en-US"),
            SelectedDatesChanged = dates => selected = dates,
        });

        h.Click(Day(h, "1/7/2026"));
        Assert.Equal(new[] { new DateTime(2026, 1, 7) }, selected!.ToArray());
        Assert.Equal("true", A(Day(h, "1/7/2026"), "data-selected"));
        Assert.Equal("false", A(Day(h, "1/7/2026"), "data-selected-single"));

        h.Click(Day(h, "1/5/2026"));
        Assert.Equal(new[] { new DateTime(2026, 1, 5), new DateTime(2026, 1, 7) }, selected!.ToArray());

        h.Click(Day(h, "1/5/2026"));
        Assert.Equal(new[] { new DateTime(2026, 1, 7) }, selected!.ToArray());
        Assert.Equal("false", A(Day(h, "1/5/2026"), "data-selected"));
        Assert.Equal("true", A(Day(h, "1/7/2026"), "data-selected"));
    }

    // ------------------------------------------------------------ months and caption layouts

    [Fact]
    public void Calendar_renders_several_months_and_respects_outside_day_visibility()
    {
        var h = Harness(() => new Calendar
        {
            NumberOfMonths = 2,
            ShowOutsideDays = false,
            DefaultMonth = January2026,
            Culture = new CultureInfo("en-US"),
        });

        Assert.Equal(2, h.FindAll(e => A(e, "data-slot") == "calendar-grid").Count);
        var captions = h.FindAll(e => A(e, "data-slot") == "calendar-caption");
        Assert.Equal(2, captions.Count);
        Assert.Equal("January 2026", captions[0].TextContent);
        Assert.Equal("February 2026", captions[1].TextContent);

        // January 2026 has 31 in-month days; February 2026 begins on Sunday and has exactly 28.
        Assert.Equal(59, h.FindAll(e => A(e, "data-slot") == "calendar-day").Count);
        Assert.Equal(4, h.FindAll(e => A(e, "data-slot") == "calendar-empty").Count);
        Assert.All(h.FindAll(e => A(e, "data-slot") == "calendar-day"), day => Assert.Null(A(day, "data-outside")));

        // Only one day across the two months is the roving tab stop.
        Assert.Single(h.FindAll(e => A(e, "data-slot") == "calendar-day"), day => day.TabIndex == 0);
    }

    [Fact]
    public void Calendar_dropdown_caption_renders_month_and_year_selectors()
    {
        var h = Harness(() => new Calendar
        {
            CaptionLayout = CalendarCaptionLayout.Dropdown,
            DefaultMonth = January2026,
            Culture = new CultureInfo("en-US"),
        });

        Assert.NotNull(Slot(h, "calendar-month-select"));
        Assert.NotNull(Slot(h, "calendar-year-select"));

        var monthTrigger = h.FindAll(e => A(e, "data-slot") == "native-select")
            .Single(e => e.TextContent.Trim() == "January");
        Assert.Equal("Month", A(monthTrigger, "aria-label"));

        monthTrigger.Focus();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        var march = h.FindAll(e => e.Role == Role.Option && e.IsVisible && e.TextContent.Trim() == "March").Single();
        h.Click(march);

        var grid = h.FindAll(e => e.Role == Role.Grid).Single();
        Assert.Equal("March 2026", A(grid, "aria-label"));
        Assert.Equal("March", h.FindAll(e => A(e, "data-slot") == "native-select")
            .Single(e => A(e, "aria-label") == "Month").TextContent.Trim());
    }
}
