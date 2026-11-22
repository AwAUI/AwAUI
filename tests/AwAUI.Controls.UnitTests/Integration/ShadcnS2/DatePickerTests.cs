using System;
using System.Globalization;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// Shadcn S2 <see cref="DatePicker"/> acceptance: the formatted trigger and placeholder, popover open/close with
/// <c>aria-expanded</c>, pointer and keyboard selection, the optional Clear action, disabled state, controlled selection,
/// culture-aware formatting, and min/max disabling.
/// </summary>
public class DatePickerTests
{
    static readonly DateTime January2026 = new(2026, 1, 1);
    static readonly CultureInfo EnUs = new("en-US");

    static DatePickerTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element Trigger(UIHarness h) =>
        h.FindAll(e => A(e, "data-slot") == "date-picker-trigger").Single();

    static Element? Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault();

    static Element Day(UIHarness h, string day) =>
        h.FindAll(e => A(e, "data-slot") == "calendar-day" && A(e, "data-day") == day).Single();

    static bool IsOpen(UIHarness h) => h.FindAll(e => e.Role == Role.Grid).Count > 0;

    // ------------------------------------------------------------ trigger and popover

    [Fact]
    public void DatePicker_shows_a_placeholder_and_opens_the_calendar()
    {
        var h = Harness(() => new DatePicker
        {
            DefaultDisplayDate = January2026,
            Culture = EnUs,
        });

        var trigger = Trigger(h);
        Assert.Equal("Pick a date", trigger.TextContent);
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.Equal("dialog", A(trigger, "aria-haspopup"));
        Assert.Equal("", A(trigger, "data-placeholder"));
        Assert.False(IsOpen(h));

        h.Click(trigger);

        Assert.Equal("true", A(Trigger(h), "aria-expanded"));
        Assert.True(IsOpen(h));
        Assert.Equal("January 2026", Slot(h, "calendar-caption")!.TextContent);
    }

    [Fact]
    public void DatePicker_selects_with_pointer_and_closes_the_popover()
    {
        DateTime? selected = null;
        var h = Harness(() => new DatePicker
        {
            DefaultDisplayDate = January2026,
            Culture = EnUs,
            SelectedDateChanged = value => selected = value,
        });

        h.Click(Trigger(h));
        h.Click(Day(h, "1/15/2026"));

        Assert.Equal(new DateTime(2026, 1, 15), selected);
        Assert.Equal("1/15/2026", Trigger(h).TextContent);
        Assert.Null(A(Trigger(h), "data-placeholder"));
        Assert.False(IsOpen(h));
        Assert.Equal("false", A(Trigger(h), "aria-expanded"));
    }

    [Fact]
    public void DatePicker_clear_action_clears_the_selection()
    {
        DateTime? selected = new DateTime(2026, 1, 15);
        var h = Harness(() => new DatePicker
        {
            DefaultSelectedDate = new DateTime(2026, 1, 15),
            DefaultDisplayDate = January2026,
            Culture = EnUs,
            ShowClear = true,
            SelectedDateChanged = value => selected = value,
        });

        Assert.Equal("1/15/2026", Trigger(h).TextContent);

        h.Click(Trigger(h));
        var clear = Slot(h, "date-picker-clear");
        Assert.NotNull(clear);
        Assert.Equal("Clear", clear!.TextContent);
        h.Click(clear);

        Assert.Null(selected);
        Assert.Equal("Pick a date", Trigger(h).TextContent);
        Assert.False(IsOpen(h));
    }

    // ------------------------------------------------------------ keyboard

    [Fact]
    public void DatePicker_opens_and_selects_with_the_keyboard()
    {
        DateTime? selected = null;
        var h = Harness(() => new DatePicker
        {
            DefaultDisplayDate = January2026,
            Culture = EnUs,
            SelectedDateChanged = value => selected = value,
        });

        Trigger(h).Focus();
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.True(IsOpen(h));

        var tabStop = h.FindAll(e => A(e, "data-slot") == "calendar-day" && e.TabIndex == 0).Single();
        Assert.Equal("1/1/2026", A(tabStop, "data-day"));
        tabStop.Focus();

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        h.Key(PhysicalKey.Enter, "Enter");

        Assert.Equal(new DateTime(2026, 1, 2), selected);
        Assert.Equal("1/2/2026", Trigger(h).TextContent);
        Assert.False(IsOpen(h));
    }

    [Fact]
    public void DatePicker_arrow_down_opens_the_calendar()
    {
        var h = Harness(() => new DatePicker
        {
            DefaultDisplayDate = January2026,
            Culture = EnUs,
        });

        Trigger(h).Focus();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        Assert.True(IsOpen(h));
        Assert.Equal("true", A(Trigger(h), "aria-expanded"));
    }

    // ------------------------------------------------------------ disabled and controlled

    [Fact]
    public void DatePicker_disabled_does_not_open()
    {
        var h = Harness(() => new DatePicker
        {
            Disabled = true,
            DefaultDisplayDate = January2026,
            Culture = EnUs,
        });

        var trigger = Trigger(h);
        Assert.Equal("true", A(trigger, "aria-disabled"));
        h.Click(trigger);
        trigger.Focus();
        h.Key(PhysicalKey.Enter, "Enter");

        Assert.False(IsOpen(h));
        Assert.Equal("false", A(Trigger(h), "aria-expanded"));
    }

    [Fact]
    public void DatePicker_controlled_selection_reports_without_switching()
    {
        DateTime? reported = null;
        var h = Harness(() => new DatePicker
        {
            SelectedDate = new DateTime(2026, 1, 10),
            IsSelectedDateControlled = true,
            DefaultDisplayDate = January2026,
            Culture = EnUs,
            SelectedDateChanged = value => reported = value,
        });

        Assert.Equal("1/10/2026", Trigger(h).TextContent);

        h.Click(Trigger(h));
        h.Click(Day(h, "1/12/2026"));

        Assert.Equal(new DateTime(2026, 1, 12), reported);
        // The parent did not update the property, so the controlled value is unchanged.
        Assert.Equal("1/10/2026", Trigger(h).TextContent);
        Assert.False(IsOpen(h));
    }

    [Fact]
    public void DatePicker_honors_min_max_and_the_disabled_predicate()
    {
        DateTime? selected = null;
        var h = Harness(() => new DatePicker
        {
            DefaultDisplayDate = January2026,
            Culture = EnUs,
            MinDate = new DateTime(2026, 1, 10),
            MaxDate = new DateTime(2026, 1, 20),
            DisabledDates = date => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            SelectedDateChanged = value => selected = value,
        });

        h.Click(Trigger(h));

        Assert.Equal("true", A(Day(h, "1/5/2026"), "aria-disabled"));
        Assert.Equal("true", A(Day(h, "1/10/2026"), "aria-disabled"));
        Assert.Equal("false", A(Day(h, "1/12/2026"), "aria-disabled"));

        h.Click(Day(h, "1/5/2026"));
        Assert.Null(selected);

        h.Click(Day(h, "1/12/2026"));
        Assert.Equal(new DateTime(2026, 1, 12), selected);
    }

    // ------------------------------------------------------------ culture

    [Fact]
    public void DatePicker_formats_and_localizes_with_the_culture()
    {
        var h = Harness(() => new DatePicker
        {
            DefaultSelectedDate = new DateTime(2026, 1, 15),
            DefaultDisplayDate = January2026,
            Culture = new CultureInfo("fr-FR"),
        });

        Assert.Equal("15/01/2026", Trigger(h).TextContent);

        h.Click(Trigger(h));

        Assert.Equal("janvier 2026", Slot(h, "calendar-caption")!.TextContent);
        var weekdays = h.FindAll(e => A(e, "data-slot") == "calendar-weekday");
        Assert.Equal("lu", weekdays[0].TextContent);
        Assert.Equal("di", weekdays[6].TextContent);
    }

    // ------------------------------------------------------------ range mode

    [Fact]
    public void DatePicker_range_mode_shows_a_label_and_closes_when_the_range_is_complete()
    {
        DateTime? start = null, end = null;
        var h = Harness(() => new DatePicker
        {
            Mode = CalendarSelectionMode.Range,
            RangePlaceholder = "Pick a date range",
            DefaultDisplayDate = January2026,
            Culture = EnUs,
            RangeChanged = (s, e) => { start = s; end = e; },
        });

        var trigger = Trigger(h);
        Assert.Equal("Pick a date range", trigger.TextContent);
        Assert.Equal("true", A(trigger, "data-empty"));
        Assert.Equal("range", A(trigger, "data-selection-mode"));

        h.Click(trigger);
        h.Click(Day(h, "1/15/2026"));

        // The first endpoint keeps the popover open and updates the pending label.
        Assert.Equal(new DateTime(2026, 1, 15), start);
        Assert.Null(end);
        Assert.True(IsOpen(h));
        Assert.Equal("Jan 15, 2026", Trigger(h).TextContent);
        Assert.Null(A(Trigger(h), "data-empty"));
        Assert.Null(A(Trigger(h), "data-placeholder"));

        h.Click(Day(h, "1/20/2026"));

        Assert.Equal(new DateTime(2026, 1, 20), end);
        Assert.False(IsOpen(h));
        Assert.Equal("false", A(Trigger(h), "aria-expanded"));
        Assert.Equal("Jan 15, 2026 - Jan 20, 2026", Trigger(h).TextContent);
    }

    [Fact]
    public void DatePicker_range_mode_formats_a_default_range_and_keeps_data_empty()
    {
        var h = Harness(() => new DatePicker
        {
            Mode = CalendarSelectionMode.Range,
            DefaultRangeStart = new DateTime(2026, 1, 5),
            DefaultRangeEnd = new DateTime(2026, 1, 10),
            DefaultDisplayDate = January2026,
            Culture = EnUs,
        });

        Assert.Equal("Jan 5, 2026 - Jan 10, 2026", Trigger(h).TextContent);
        Assert.Null(A(Trigger(h), "data-empty"));
    }

    [Fact]
    public void DatePicker_range_mode_supports_keyboard_navigation_and_completion()
    {
        DateTime? start = null, end = null;
        var h = Harness(() => new DatePicker
        {
            Mode = CalendarSelectionMode.Range,
            DefaultDisplayDate = January2026,
            Culture = EnUs,
            RangeChanged = (s, e) => { start = s; end = e; },
        });

        Trigger(h).Focus();
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.True(IsOpen(h));

        var tabStop = h.FindAll(e => A(e, "data-slot") == "calendar-day" && e.TabIndex == 0).Single();
        tabStop.Focus();
        h.Key(PhysicalKey.Enter, "Enter");

        Assert.Equal(new DateTime(2026, 1, 1), start);
        Assert.Null(end);
        Assert.True(IsOpen(h));

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        h.Key(PhysicalKey.Enter, "Enter");

        Assert.Equal(new DateTime(2026, 1, 2), end);
        Assert.False(IsOpen(h));
        Assert.Equal("Jan 1, 2026 - Jan 2, 2026", Trigger(h).TextContent);
    }
}
