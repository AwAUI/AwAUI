using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.Svg;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn;

/// <summary>How a <see cref="Calendar"/> turns clicks into a selection (react-day-picker's <c>mode</c>).</summary>
public enum CalendarSelectionMode
{
    /// <summary>One date at a time (<see cref="Calendar.SelectedDate"/>).</summary>
    Single,

    /// <summary>A set of independent dates (<see cref="Calendar.SelectedDates"/>).</summary>
    Multiple,

    /// <summary>A contiguous range with two endpoints (<see cref="Calendar.RangeStart"/> / <see cref="Calendar.RangeEnd"/>).</summary>
    Range,
}

/// <summary>How the month caption is rendered (react-day-picker's <c>captionLayout</c>).</summary>
public enum CalendarCaptionLayout
{
    /// <summary>A text label such as "January 2026" (the default).</summary>
    Default,

    /// <summary>Month and year selectors, as in the shadcn/ui dropdown examples.</summary>
    Dropdown,
}

/// <summary>
/// A selected range. <see cref="End"/> is <c>null</c> while only the first endpoint has been chosen, which is how a
/// range in progress is represented (the hover preview is not stored here).
/// </summary>
public readonly record struct CalendarRange(DateTime Start, DateTime? End)
{
    /// <summary>Whether both endpoints have been chosen.</summary>
    public bool IsComplete => End is not null;

    /// <summary>Returns the endpoints in ascending order.</summary>
    public CalendarRange Order()
    {
        if (End is not { } end || end >= Start) return this;
        return new CalendarRange(end, Start);
    }
}

/// <summary>
/// A month grid for choosing dates, matching shadcn/ui new-york-v4's <c>Calendar</c>.
/// Month and weekday names, the first day of the week, and the day labels come from
/// <see cref="CultureInfo.CurrentCulture"/> (or <see cref="Culture"/>).
/// <para>
/// <see cref="SelectionMode"/> selects between a single date (<see cref="SelectedDate"/>), a set of dates
/// (<see cref="SelectedDates"/>), and a range (<see cref="RangeStart"/> / <see cref="RangeEnd"/>). Every mode can be
/// controlled through its value and <c>…Changed</c> callback or uncontrolled through a <c>Default…</c> value. In range mode
/// the first click anchors the range, hovering previews it with <c>data-range-*</c> attributes, and the second click
/// completes it (clicking an earlier day swaps the endpoints).
/// </para>
/// <para>
/// The visible month is controlled through <see cref="DisplayDate"/> (alias <see cref="Month"/>) or starts at
/// <see cref="DefaultDisplayDate"/> (alias <see cref="DefaultMonth"/>); <see cref="NumberOfMonths"/> renders several
/// consecutive months. Days outside the month, today, disabled dates, and an optional date predicate are reflected in
/// <c>data-*</c> attributes and ARIA state. Keyboard navigation (arrows, Home/End, PageUp/PageDown, Enter/Space) uses a
/// single roving tab stop that follows the focused date.
/// </para>
/// </summary>
public sealed partial class Calendar : Component
{
    const string RootClass =
        "group/calendar w-fit bg-background p-3 text-foreground " +
        "[[data-slot=card-content]_&]:bg-transparent [[data-slot=popover-content]_&]:bg-transparent";

    readonly CalendarScope _scope = new();

    /// <summary>Selection model. Defaults to <see cref="CalendarSelectionMode.Single"/>.</summary>
    public CalendarSelectionMode SelectionMode { get; set; } = CalendarSelectionMode.Single;

    // ---------------------------------------------------------------- single

    /// <summary>Controlled selection. A value only takes effect when non-null or <see cref="IsSelectedDateControlled"/> is set.</summary>
    public DateTime? SelectedDate { get; set; }

    /// <summary>Selection used before the user picks a date (uncontrolled).</summary>
    public DateTime? DefaultSelectedDate { get; set; }

    /// <summary>Treats <see cref="SelectedDate"/> as controlled even when it is <c>null</c> (no selection).</summary>
    public bool IsSelectedDateControlled { get; set; }

    /// <summary>Receives each single-date selection and supports <c>@bind-SelectedDate</c>.</summary>
    public Action<DateTime?>? SelectedDateChanged { get; set; }

    /// <summary>Alias for <see cref="SelectedDateChanged"/>.</summary>
    public Action<DateTime?>? OnSelect { get; set; }

    // ---------------------------------------------------------------- range

    /// <summary>Controlled range start (null while no range is selected).</summary>
    public DateTime? RangeStart { get; set; }

    /// <summary>Controlled range end; null while the second endpoint has not been chosen yet.</summary>
    public DateTime? RangeEnd { get; set; }

    /// <summary>Range used before the user picks dates (uncontrolled).</summary>
    public DateTime? DefaultRangeStart { get; set; }

    /// <summary>Second endpoint of the uncontrolled default range.</summary>
    public DateTime? DefaultRangeEnd { get; set; }

    /// <summary>Treats <see cref="RangeStart"/> / <see cref="RangeEnd"/> as controlled even when both are <c>null</c>.</summary>
    public bool IsRangeControlled { get; set; }

    /// <summary>Receives each range change as (start, end); end is null while only the first endpoint is chosen.</summary>
    public Action<DateTime?, DateTime?>? RangeChanged { get; set; }

    /// <summary>Alias for <see cref="RangeChanged"/>.</summary>
    public Action<DateTime?, DateTime?>? OnRangeChange { get; set; }

    // ---------------------------------------------------------------- multiple

    /// <summary>Controlled set of selected dates in <see cref="CalendarSelectionMode.Multiple"/>.</summary>
    public IReadOnlyList<DateTime>? SelectedDates { get; set; }

    /// <summary>Initially selected dates in <see cref="CalendarSelectionMode.Multiple"/> (uncontrolled).</summary>
    public IReadOnlyList<DateTime>? DefaultSelectedDates { get; set; }

    /// <summary>Treats <see cref="SelectedDates"/> as controlled even when it is <c>null</c>.</summary>
    public bool IsSelectedDatesControlled { get; set; }

    /// <summary>Receives the sorted selected dates in <see cref="CalendarSelectionMode.Multiple"/>.</summary>
    public Action<IReadOnlyList<DateTime>>? SelectedDatesChanged { get; set; }

    /// <summary>Alias for <see cref="SelectedDatesChanged"/>.</summary>
    public Action<IReadOnlyList<DateTime>>? OnSelectedDatesChange { get; set; }

    // ---------------------------------------------------------------- month control

    /// <summary>Controlled visible month (any day of the month). Null leaves navigation uncontrolled.</summary>
    public DateTime? DisplayDate { get; set; }

    /// <summary>Month shown before the user navigates (uncontrolled). Defaults to the selection, then today.</summary>
    public DateTime? DefaultDisplayDate { get; set; }

    /// <summary>Receives month navigation and supports <c>@bind-DisplayDate</c>.</summary>
    public Action<DateTime>? DisplayDateChanged { get; set; }

    /// <summary>Alias for <see cref="DisplayDate"/> (react-day-picker's <c>month</c>).</summary>
    public DateTime? Month { get; set; }

    /// <summary>Alias for <see cref="DefaultDisplayDate"/> (react-day-picker's <c>defaultMonth</c>).</summary>
    public DateTime? DefaultMonth { get; set; }

    /// <summary>Receives month navigation and supports <c>@bind-Month</c>.</summary>
    public Action<DateTime>? MonthChanged { get; set; }

    /// <summary>Alias for <see cref="DisplayDateChanged"/>.</summary>
    public Action<DateTime>? OnMonthChange { get; set; }

    /// <summary>Number of consecutive months to render. Default 1.</summary>
    public int NumberOfMonths { get; set; } = 1;

    /// <summary>How the month caption is rendered.</summary>
    public CalendarCaptionLayout CaptionLayout { get; set; }

    // ---------------------------------------------------------------- date bounds

    /// <summary>Earliest selectable date (inclusive).</summary>
    public DateTime? MinDate { get; set; }

    /// <summary>Latest selectable date (inclusive).</summary>
    public DateTime? MaxDate { get; set; }

    /// <summary>Alias for <see cref="MinDate"/> (react-day-picker's <c>fromDate</c>).</summary>
    public DateTime? FromDate { get; set; }

    /// <summary>Alias for <see cref="MaxDate"/> (react-day-picker's <c>toDate</c>).</summary>
    public DateTime? ToDate { get; set; }

    /// <summary>Marks individual dates as disabled (for example weekends or holidays).</summary>
    public Func<DateTime, bool>? DisabledDates { get; set; }

    /// <summary>Alias for <see cref="DisabledDates"/> (react-day-picker's <c>disabled</c> predicate).</summary>
    public Func<DateTime, bool>? IsDateDisabled { get; set; }

    // ---------------------------------------------------------------- grid

    /// <summary>Renders the leading and trailing days of adjacent months. Default true.</summary>
    public bool ShowOutsideDays { get; set; } = true;

    /// <summary>Adds a week-number column.</summary>
    public bool ShowWeekNumbers { get; set; }

    /// <summary>Alias for <see cref="ShowWeekNumbers"/> (react-day-picker's <c>showWeekNumber</c>).</summary>
    public bool ShowWeekNumber { get; set; }

    /// <summary>Overrides the culture's first day of the week.</summary>
    public DayOfWeek? FirstDayOfWeek { get; set; }

    /// <summary>First day of the week as 0 = Sunday … 6 = Saturday (react-day-picker's <c>weekStartsOn</c>).</summary>
    public int? WeekStartsOn { get; set; }

    /// <summary>Culture used for month, weekday, and day labels. Defaults to <see cref="CultureInfo.CurrentCulture"/>.</summary>
    public CultureInfo? Culture { get; set; }

    /// <summary>Text direction for the arrow keys. Defaults to the computed <c>direction</c>.</summary>
    public Direction? Dir { get; set; }

    /// <summary>Disables the whole calendar.</summary>
    public bool Disabled { get; set; }

    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }

    /// <summary>Custom parts. When omitted, a <see cref="CalendarNavigation"/> and one <see cref="CalendarGrid"/> per month are rendered.</summary>
    public NodeList? Children { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var culture = Culture ?? CultureInfo.CurrentCulture;
        var firstDay = ResolveFirstDayOfWeek(culture);

        DateTime? selected = _scope.Selection.Sync(
            IsSelectedDateControlled || SelectedDate is not null,
            SelectedDate,
            DefaultSelectedDate,
            SelectedDateChanged + OnSelect);
        selected = selected?.Date;

        CalendarRange? controlledRange = ControlledRange();
        CalendarRange? defaultRange = DefaultRange();
        CalendarRange? range = _scope.Range.Sync(
            IsRangeControlled || controlledRange is not null,
            controlledRange,
            defaultRange,
            changed =>
            {
                var handler = RangeChanged + OnRangeChange;
                if (handler is null) return;
                if (changed is { } value) handler(value.Start, value.End);
                else handler(null, null);
            });

        IReadOnlyList<DateTime>? controlledDates = NormalizeDates(SelectedDates);
        IReadOnlyList<DateTime>? defaultDates = NormalizeDates(DefaultSelectedDates);
        IReadOnlyList<DateTime>? multiple = _scope.Multiple.Sync(
            IsSelectedDatesControlled || controlledDates is not null,
            controlledDates,
            defaultDates,
            dates => (SelectedDatesChanged + OnSelectedDatesChange)?.Invoke(dates ?? Array.Empty<DateTime>()));

        DateTime visible = _scope.Visible.Sync(
            DisplayDate ?? Month,
            FirstOfMonth(DefaultDisplayDate ?? DefaultMonth ?? selected ?? range?.Start ?? DateTime.Today),
            DisplayDateChanged + MonthChanged + OnMonthChange);
        visible = FirstOfMonth(visible);

        // Always read the roving date and the hover preview so navigation and range previews rebuild the grid.
        DateTime focused = _scope.Focused.Value.Date;
        DateTime? hover = _scope.Hover.Value is { } h ? h.Date : null;

        int months = Math.Max(1, NumberOfMonths);
        DateTime? min = (MinDate ?? FromDate)?.Date;
        DateTime? max = (MaxDate ?? ToDate)?.Date;
        Func<DateTime, bool>? disabledDates = Combine(DisabledDates, IsDateDisabled);
        bool showWeekNumbers = ShowWeekNumbers || ShowWeekNumber;

        var context = new CalendarContext(_scope, visible, selected, range, multiple, hover, focused, culture, firstDay,
            min, max, disabledDates, ShowOutsideDays, showWeekNumbers, Disabled, Dir, SelectionMode, months, CaptionLayout);
        _scope.Context = context;

        // Refs are rebuilt every frame; clear once for all month grids before they render.
        _scope.DayRefs.Clear();

        var root = TabsParts.Style(new Div { Ref = Ref, Children = Children ?? DefaultParts(months) },
            "calendar", RootClass, Class, Rest);
        root.Attributes["data-disabled"] = Disabled ? "" : null;
        root.Attributes["data-selection-mode"] = ModeName(SelectionMode);

        // Follow an externally controlled selection while the user has not moved the roving focus.
        UseEffect(() =>
        {
            if (SelectionMode != CalendarSelectionMode.Single) return null;
            // The grid normally consumes the request; clear it here when custom parts have no grid.
            if (_scope.UserMovedFocus)
            {
                _scope.UserMovedFocus = false;
                return null;
            }
            if (selected is not { } value) return null;
            if (_scope.Focused.Peek().Date == value) return null;
            if (DisplayDate is null && Month is null && (value.Year != visible.Year || value.Month != visible.Month))
                _scope.Visible.Set(new DateTime(value.Year, value.Month, 1));
            _scope.Focused.Value = value;
            return null;
        }, selected);

        return Provide(context, root);
    }

    protected override void OnMount()
    {
        _scope.UserMovedFocus = false;
        if (_scope.Context is not { } context) return;
        _scope.Focused.Value = CalendarParts.InitialFocus(context);
    }

    CalendarRange? ControlledRange()
    {
        if (RangeStart is { } start) return new CalendarRange(start.Date, RangeEnd?.Date);
        if (RangeEnd is { } end) return new CalendarRange(end.Date, end.Date);
        return null;
    }

    CalendarRange? DefaultRange()
    {
        if (DefaultRangeStart is { } start) return new CalendarRange(start.Date, DefaultRangeEnd?.Date);
        if (DefaultRangeEnd is { } end) return new CalendarRange(end.Date, end.Date);
        return null;
    }

    DayOfWeek ResolveFirstDayOfWeek(CultureInfo culture)
    {
        if (WeekStartsOn is { } weekStart)
            return (DayOfWeek)(((weekStart % 7) + 7) % 7);
        return FirstDayOfWeek ?? culture.DateTimeFormat.FirstDayOfWeek;
    }

    static Func<DateTime, bool>? Combine(Func<DateTime, bool>? left, Func<DateTime, bool>? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return date => left(date) || right(date);
    }

    static IReadOnlyList<DateTime>? NormalizeDates(IReadOnlyList<DateTime>? dates)
    {
        if (dates is null) return null;
        return dates.Select(static d => d.Date).Distinct().OrderBy(static d => d).ToList();
    }

    static NodeList DefaultParts(int months)
    {
        var parts = new NodeList { new CalendarNavigation() };
        if (months <= 1)
        {
            parts.Add(new CalendarGrid());
            return parts;
        }

        var row = TabsParts.Style(new Div(), "calendar-months", "flex flex-col gap-4 md:flex-row", null, null);
        for (int index = 0; index < months; index++)
            row.Children.Add(new CalendarGrid { MonthOffset = index });
        parts.Add(row);
        return parts;
    }

    static string ModeName(CalendarSelectionMode mode) => mode switch
    {
        CalendarSelectionMode.Range => "range",
        CalendarSelectionMode.Multiple => "multiple",
        _ => "single",
    };

    static DateTime FirstOfMonth(DateTime value) => new(value.Year, value.Month, 1);
}

/// <summary>The month/year toolbar with previous/next controls and a live caption (one caption per visible month).</summary>
public sealed partial class CalendarNavigation : Component
{
    const string NavigationClass = "relative flex w-full items-center justify-between gap-1";
    const string GroupClass = "flex items-center gap-1";
    const string CaptionsClass = "flex flex-1 items-center gap-2";
    const string CaptionClass = "flex h-8 flex-1 items-center justify-center px-2 text-sm font-medium select-none";

    public string? Class { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var ctx = Use<CalendarContext>();
        var scope = ctx.Scope;

        var previousYear = CalendarParts.NavButton(ctx, "«", "calendar-previous-year", "Previous year",
            () => Navigate(scope, years: -1), atBoundary: true);
        var previousMonth = CalendarParts.NavButton(ctx, null, "calendar-previous-month", "Previous month",
            () => Navigate(scope, months: -1), atBoundary: true, icon: LucideIcons.ChevronLeft);
        var nextMonth = CalendarParts.NavButton(ctx, null, "calendar-next-month", "Next month",
            () => Navigate(scope, months: 1), atBoundary: false, icon: LucideIcons.ChevronRight);
        var nextYear = CalendarParts.NavButton(ctx, "»", "calendar-next-year", "Next year",
            () => Navigate(scope, years: 1), atBoundary: false);

        var captions = new NodeList();
        for (int index = 0; index < ctx.NumberOfMonths; index++)
        {
            var month = ctx.VisibleMonth.AddMonths(index);
            if (ctx.CaptionLayout == CalendarCaptionLayout.Dropdown)
            {
                captions.Add(new CalendarCaptionDropdown { MonthOffset = index });
                continue;
            }

            var caption = TabsParts.Style(new Div { Children = new NodeList { new UIText(CalendarParts.Caption(ctx, month)) } },
                "calendar-caption", CaptionClass, null, null);
            caption.Attributes["aria-live"] = "polite";
            captions.Add(caption);
        }

        var captionHost = TabsParts.Style(new Div { Children = captions }, "calendar-captions", CaptionsClass, null, null);

        var nav = TabsParts.Style(new Div
        {
            Children = new NodeList
            {
                TabsParts.Style(new Div { Children = new NodeList { previousYear, previousMonth } }, "calendar-navigation-start", GroupClass, null, null),
                captionHost,
                TabsParts.Style(new Div { Children = new NodeList { nextMonth, nextYear } }, "calendar-navigation-end", GroupClass, null, null),
            },
        }, "calendar-navigation", NavigationClass, Class, Rest);

        return nav;
    }

    static void Navigate(CalendarScope scope, int months = 0, int years = 0)
    {
        var focused = scope.Focused.Peek();
        if (years != 0) scope.MoveYears(focused, years);
        else scope.MoveMonths(focused, months);
        // Navigation buttons keep focus on themselves; only the roving date (and tab stop) moves.
        scope.UserMovedFocus = false;
    }
}

/// <summary>The month and year selectors shown when <see cref="CalendarCaptionLayout.Dropdown"/> is used.</summary>
internal sealed partial class CalendarCaptionDropdown : Component
{
    const string WrapperClass = "flex flex-1 items-center justify-center gap-1";
    const string SelectClass =
        "h-7 w-fit min-w-0 rounded-md border-0 bg-transparent px-2 py-0 text-sm font-medium shadow-none " +
        "focus-visible:border-0 focus-visible:ring-0 dark:bg-transparent dark:hover:bg-accent";

    /// <summary>Index of the month this caption belongs to (0 for the first visible month).</summary>
    public int MonthOffset { get; set; }

    protected override Node Build()
    {
        var ctx = Use<CalendarContext>();
        var month = ctx.VisibleMonth.AddMonths(MonthOffset);

        var months = new NodeList();
        for (int value = 1; value <= 12; value++)
        {
            months.Add(new NativeSelectOption
            {
                Value = value.ToString(CultureInfo.InvariantCulture),
                Children = new NodeList { new UIText(ctx.Culture.DateTimeFormat.GetMonthName(value)) },
            });
        }

        int firstYear = ctx.MinDate?.Year ?? Math.Min(month.Year - 10, DateTime.Today.Year - 10);
        int lastYear = ctx.MaxDate?.Year ?? Math.Max(month.Year + 10, DateTime.Today.Year + 10);
        if (firstYear > month.Year) firstYear = month.Year;
        if (lastYear < month.Year) lastYear = month.Year;

        var years = new NodeList();
        for (int year = firstYear; year <= lastYear; year++)
        {
            years.Add(new NativeSelectOption
            {
                Value = year.ToString(CultureInfo.InvariantCulture),
                Children = new NodeList { new UIText(year.ToString(ctx.Culture)) },
            });
        }

        var monthSelect = new NativeSelect
        {
            Value = month.Month.ToString(CultureInfo.InvariantCulture),
            Class = SelectClass,
            Rest = new AttributeDictionary { ["aria-label"] = "Month" },
            Children = months,
        };
        monthSelect.ValueChanged = value =>
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selected) && selected is >= 1 and <= 12)
                ctx.Scope.SetVisibleMonth(new DateTime(month.Year, selected, 1));
        };

        var yearSelect = new NativeSelect
        {
            Value = month.Year.ToString(CultureInfo.InvariantCulture),
            Class = SelectClass,
            Rest = new AttributeDictionary { ["aria-label"] = "Year" },
            Children = years,
        };
        yearSelect.ValueChanged = value =>
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selected))
                ctx.Scope.SetVisibleMonth(new DateTime(selected, month.Month, 1));
        };

        var wrapper = TabsParts.Style(new Div(), "calendar-caption", WrapperClass, null, null);
        wrapper.Attributes["aria-live"] = "polite";
        wrapper.Children.Add(TabsParts.Style(new Div { Children = new NodeList { monthSelect } }, "calendar-month-select", "w-fit", null, null));
        wrapper.Children.Add(TabsParts.Style(new Div { Children = new NodeList { yearSelect } }, "calendar-year-select", "w-fit", null, null));
        return wrapper;
    }
}

/// <summary>The weekday header and the month's weeks of day buttons. <see cref="MonthOffset"/> selects the month within a multi-month calendar.</summary>
public sealed partial class CalendarGrid : Component
{
    const string GridClass = "w-full border-collapse";
    const string WeekdaysClass = "flex w-full";
    const string WeekdayClass =
        "flex size-8 items-center justify-center rounded-md text-[0.8rem] font-normal text-muted-foreground select-none";
    const string WeekClass = "mt-2 flex w-full";
    const string WeekNumberClass = "flex size-8 items-center justify-center text-[0.8rem] text-muted-foreground select-none";
    const string DayClass =
        "relative flex size-8 items-center justify-center rounded-md p-0 text-center text-sm font-normal whitespace-nowrap " +
        "transition-colors outline-none select-none hover:bg-accent hover:text-accent-foreground " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:pointer-events-none " +
        "data-[range-middle=true]:rounded-none data-[range-middle=true]:bg-accent data-[range-middle=true]:text-accent-foreground " +
        "data-[range-start=true]:rounded-l-md data-[range-end=true]:rounded-r-md";
    const string OutsideClass = "text-muted-foreground";
    const string TodayClass = "bg-accent text-accent-foreground";
    const string SelectedClass = "bg-primary text-primary-foreground hover:bg-primary/90 hover:text-primary-foreground";
    const string RangeMiddleClass = "bg-accent text-accent-foreground hover:bg-accent hover:text-accent-foreground";
    const string DisabledClass = "text-muted-foreground opacity-50";
    const string EmptyClass = "size-8";

    /// <summary>Month index rendered by this grid, relative to the calendar's visible month. Default 0.</summary>
    public int MonthOffset { get; set; }

    public string? Class { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var ctx = Use<CalendarContext>();
        var month = ctx.VisibleMonth.AddMonths(MonthOffset);
        var monthLabel = CalendarParts.Caption(ctx, month);

        var grid = TabsParts.Style(new Div { Role = Role.Grid, AriaLabel = monthLabel },
            "calendar-grid", GridClass, Class, Rest);
        grid.Children.Add(BuildWeekdays(ctx));

        var first = month;
        int offset = ((int)first.DayOfWeek - (int)ctx.FirstDayOfWeek + 7) % 7;
        var start = first.AddDays(-offset);
        int daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
        int weeks = (offset + daysInMonth + 6) / 7;

        bool tabStopInMonth = ctx.Focused.Year == first.Year && ctx.Focused.Month == first.Month
            && !ctx.IsDisabled(ctx.Focused);
        DateTime tabStop;
        if (tabStopInMonth)
            tabStop = ctx.Focused;
        else if (!ctx.FocusedInVisibleMonths && MonthOffset == 0)
            tabStop = CalendarParts.FirstEnabled(ctx, month) ?? first;
        else
            tabStop = DateTime.MinValue;

        for (int week = 0; week < weeks; week++)
        {
            var row = TabsParts.Style(new Div { Role = Role.Row }, "calendar-week", WeekClass, null, null);
            if (ctx.ShowWeekNumbers)
            {
                var weekDate = start.AddDays(week * 7);
                int number = CalendarParts.WeekNumber(ctx, weekDate);
                var weekCell = TabsParts.Style(new Div
                {
                    Role = Role.Cell,
                    AriaLabel = "Week " + number.ToString(ctx.Culture),
                    Children = new NodeList { new UIText(number.ToString(ctx.Culture)) },
                }, "calendar-week-number", WeekNumberClass, null, null);
                row.Children.Add(weekCell);
            }

            for (int day = 0; day < 7; day++)
            {
                var date = start.AddDays(week * 7 + day);
                bool inMonth = date.Year == first.Year && date.Month == first.Month;
                if (!inMonth && !ctx.ShowOutsideDays)
                {
                    var empty = TabsParts.Style(new Div { Role = Role.Cell }, "calendar-empty", EmptyClass, null, null);
                    empty.Attributes["aria-hidden"] = "true";
                    row.Children.Add(empty);
                    continue;
                }
                row.Children.Add(BuildDay(ctx, date, inMonth, tabStop));
            }

            grid.Children.Add(row);
        }

        UseEffect(() =>
        {
            CalendarParts.FocusIfRequested(ctx);
            return null;
        }, ctx.Focused);

        return grid;
    }

    static Node BuildWeekdays(CalendarContext ctx)
    {
        var row = TabsParts.Style(new Div { Role = Role.Row }, "calendar-weekdays", WeekdaysClass, null, null);
        if (ctx.ShowWeekNumbers)
        {
            var corner = TabsParts.Style(new Div { Role = Role.Cell }, "calendar-week-number-header", WeekNumberClass, null, null);
            corner.Attributes["aria-hidden"] = "true";
            row.Children.Add(corner);
        }
        for (int index = 0; index < 7; index++)
        {
            var dayOfWeek = (DayOfWeek)(((int)ctx.FirstDayOfWeek + index) % 7);
            var name = ctx.Culture.DateTimeFormat.ShortestDayNames[(int)dayOfWeek];
            var cell = TabsParts.Style(new Div
            {
                Role = Role.Cell,
                AriaLabel = ctx.Culture.DateTimeFormat.GetDayName(dayOfWeek),
                Children = new NodeList { new UIText(name) },
            }, "calendar-weekday", WeekdayClass, null, null);
            row.Children.Add(cell);
        }
        return row;
    }

    static UIButton BuildDay(CalendarContext ctx, DateTime date, bool inMonth, DateTime tabStop)
    {
        var day = date.Date;
        bool disabled = ctx.Disabled || ctx.IsDisabled(day);
        bool today = day == DateTime.Today;
        bool focused = !disabled && tabStop != DateTime.MinValue && day == tabStop.Date;

        var range = ctx.VisibleRange;
        bool isStart = false, isEnd = false, isMiddle = false;
        if (range is { } current)
        {
            isStart = day == current.Start.Date;
            isEnd = current.End is { } end && day == end.Date;
            isMiddle = current.End is { } rangeEnd && day > current.Start.Date && day < rangeEnd.Date;
        }
        bool inRange = isStart || isEnd || isMiddle;
        bool rangePreview = inRange && ctx.IsRangePreviewing;

        bool selected = ctx.Mode switch
        {
            CalendarSelectionMode.Range => inRange,
            CalendarSelectionMode.Multiple => ctx.IsMultipleSelected(day),
            _ => ctx.Selected is { } selection && selection.Date == day,
        };

        var button = new UIButton
        {
            Role = Role.Cell,
            Disabled = disabled,
            TabIndex = focused ? 0 : -1,
            AriaLabel = date.ToString("D", ctx.Culture),
            Children = new NodeList { new UIText(date.Day.ToString(ctx.Culture)) },
        };
        button.Class = TailwindMerge.Cn(DayClass,
            inMonth ? null : OutsideClass,
            today ? TodayClass : null,
            isMiddle ? RangeMiddleClass : null,
            selected && !isMiddle ? SelectedClass : null,
            disabled ? DisabledClass : null);
        button.Attributes["type"] = "button";
        button.Attributes["data-slot"] = "calendar-day";
        button.Attributes["data-day"] = date.ToString("d", ctx.Culture);
        button.Attributes["data-selected"] = TabsParts.Bool(selected);
        button.Attributes["data-selected-single"] = TabsParts.Bool(ctx.Mode == CalendarSelectionMode.Single && selected);
        button.Attributes["data-range-start"] = isStart ? "true" : null;
        button.Attributes["data-range-middle"] = isMiddle ? "true" : null;
        button.Attributes["data-range-end"] = isEnd ? "true" : null;
        button.Attributes["data-range-preview"] = rangePreview ? "true" : null;
        button.Attributes["data-today"] = today ? "" : null;
        button.Attributes["data-outside"] = inMonth ? null : "";
        button.Attributes["data-disabled"] = disabled ? "" : null;
        button.Attributes["aria-selected"] = TabsParts.Bool(selected);
        button.Attributes["aria-disabled"] = TabsParts.Bool(disabled);
        button.Attributes["aria-current"] = today ? "date" : null;

        var reference = new Ref<Element>();
        ctx.Scope.DayRefs[day] = reference;
        button.Ref = reference;

        if (ctx.Mode == CalendarSelectionMode.Range)
        {
            button.OnPointerEnter = EventComposition.Compose(button.OnPointerEnter, _ => Preview(ctx, day));
            button.OnPointerMove = EventComposition.Compose(button.OnPointerMove, _ => Preview(ctx, day));
        }

        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (e.DefaultPrevented) return;
            ctx.Scope.Activate(day);
        });
        button.OnKeyDown = EventComposition.Compose(button.OnKeyDown, e => HandleKey(e, ctx, day));
        return button;
    }

    static void Preview(CalendarContext ctx, DateTime date)
    {
        if (ctx.Disabled || ctx.Mode != CalendarSelectionMode.Range) return;
        if (ctx.Scope.Hover.Peek() is { } current && current.Date == date) return;
        ctx.Scope.Hover.Value = date;
    }

    static void HandleKey(KeyEvent e, CalendarContext ctx, DateTime date)
    {
        if (e.DefaultPrevented || ctx.Disabled) return;
        if ((e.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        var scope = ctx.Scope;
        switch (e.Key)
        {
            case "ArrowLeft":
                e.PreventDefault();
                scope.MoveDays(date, TabsParts.ResolveDirection(e.CurrentTarget, ctx.Dir) == Direction.Rtl ? 1 : -1);
                break;
            case "ArrowRight":
                e.PreventDefault();
                scope.MoveDays(date, TabsParts.ResolveDirection(e.CurrentTarget, ctx.Dir) == Direction.Rtl ? -1 : 1);
                break;
            case "ArrowUp":
                e.PreventDefault();
                scope.MoveDays(date, -7);
                break;
            case "ArrowDown":
                e.PreventDefault();
                scope.MoveDays(date, 7);
                break;
            case "Home":
                e.PreventDefault();
                scope.MoveWeekEdge(date, end: false);
                break;
            case "End":
                e.PreventDefault();
                scope.MoveWeekEdge(date, end: true);
                break;
            case "PageUp":
                e.PreventDefault();
                if (shift) scope.MoveYears(date, -1);
                else scope.MoveMonths(date, -1);
                break;
            case "PageDown":
                e.PreventDefault();
                if (shift) scope.MoveYears(date, 1);
                else scope.MoveMonths(date, 1);
                break;
        }
    }
}

/// <summary>Month navigation, roving focus, selection modes, and disabled-date logic shared by the calendar parts.</summary>
internal sealed class CalendarScope
{
    internal readonly ControllableState<DateTime?> Selection = new();
    internal readonly ControllableState<CalendarRange?> Range = new();
    internal readonly ControllableState<IReadOnlyList<DateTime>?> Multiple = new();
    internal readonly ControllableState<DateTime> Visible = new();
    internal readonly State<DateTime> Focused = new(DateTime.Today);
    internal readonly State<DateTime?> Hover = new((DateTime?)null);
    internal readonly Dictionary<DateTime, Ref<Element>> DayRefs = new();

    internal CalendarContext? Context { get; set; }

    /// <summary>Set when the user navigated with the keyboard or a day button so the next commit moves DOM focus.</summary>
    internal bool UserMovedFocus { get; set; }

    internal void Activate(DateTime date)
    {
        if (Context is not { } ctx || ctx.Disabled || ctx.IsDisabled(date)) return;
        date = date.Date;

        switch (ctx.Mode)
        {
            case CalendarSelectionMode.Range:
                SetRange(date);
                break;
            case CalendarSelectionMode.Multiple:
                ToggleMultiple(date);
                break;
            default:
                Selection.Set(date);
                break;
        }

        // A completed or restarted interaction never leaves a stale hover preview behind.
        if (Hover.Peek() is not null) Hover.Value = null;
        SetFocus(date, requestFocus: true);
    }

    void SetRange(DateTime date)
    {
        var current = Range.Value;
        CalendarRange next;
        if (current is not { } pending || pending.End is not null)
        {
            // Start a new range; the end is filled by the next click.
            next = new CalendarRange(date, null);
        }
        else
        {
            var start = pending.Start.Date;
            if (date < start) next = new CalendarRange(date, start);
            else if (date == start) next = new CalendarRange(start, start);
            else next = new CalendarRange(start, date);
        }
        Range.Set(next);
    }

    void ToggleMultiple(DateTime date)
    {
        var list = Multiple.Value is { } existing ? new List<DateTime>(existing) : new List<DateTime>();
        if (!list.Remove(date)) list.Add(date);
        list.Sort();
        Multiple.Set(list);
    }

    internal void SetVisibleMonth(DateTime month)
    {
        var target = new DateTime(month.Year, month.Month, 1);
        Visible.Set(target);
        var focused = Focused.Peek();
        if (focused.Year != target.Year || focused.Month != target.Month)
        {
            int day = Math.Min(focused.Day, DateTime.DaysInMonth(target.Year, target.Month));
            Focused.Value = new DateTime(target.Year, target.Month, day);
        }
        UserMovedFocus = false;
    }

    internal void MoveDays(DateTime from, int days)
    {
        if (days == 0) return;
        if (FindEnabled(from.AddDays(days), Math.Sign(days)) is { } target) SetFocus(target, requestFocus: true);
    }

    internal void MoveMonths(DateTime from, int months)
    {
        if (months == 0) return;
        if (FindEnabled(from.AddMonths(months), Math.Sign(months)) is { } target) SetFocus(target, requestFocus: true);
    }

    internal void MoveYears(DateTime from, int years)
    {
        if (years == 0) return;
        if (FindEnabled(from.AddYears(years), Math.Sign(years)) is { } target) SetFocus(target, requestFocus: true);
    }

    internal void MoveWeekEdge(DateTime from, bool end)
    {
        if (Context is not { } ctx) return;
        int offset = ((int)from.DayOfWeek - (int)ctx.FirstDayOfWeek + 7) % 7;
        var start = from.AddDays(-offset);
        var candidate = end ? start.AddDays(6) : start;
        if (FindEnabled(candidate, end ? -1 : 1) is { } target) SetFocus(target, requestFocus: true);
    }

    void SetFocus(DateTime date, bool requestFocus)
    {
        if (Context is not { } ctx) return;
        date = date.Date;
        bool changed = Focused.Peek().Date != date;
        if (date.Year != ctx.VisibleMonth.Year || date.Month != ctx.VisibleMonth.Month)
        {
            Visible.Set(new DateTime(date.Year, date.Month, 1));
            changed = true;
        }
        Focused.Value = date;
        UserMovedFocus = requestFocus && changed;
    }

    DateTime? FindEnabled(DateTime start, int direction)
    {
        if (Context is not { } ctx) return null;
        if (direction == 0) direction = 1;
        var date = start.Date;
        // Bound the scan so an all-disabled range cannot loop forever.
        for (int step = 0; step < 400; step++)
        {
            if (ctx.MinDate is { } min && date < min) return null;
            if (ctx.MaxDate is { } max && date > max) return null;
            if (!ctx.IsDisabled(date)) return date;
            date = date.AddDays(direction);
        }
        return null;
    }
}

/// <summary>Values read by <see cref="CalendarNavigation"/> and <see cref="CalendarGrid"/> for the current frame.</summary>
internal sealed class CalendarContext
{
    internal CalendarContext(CalendarScope scope, DateTime visibleMonth, DateTime? selected, CalendarRange? range,
        IReadOnlyList<DateTime>? multiple, DateTime? hover, DateTime focused, CultureInfo culture,
        DayOfWeek firstDayOfWeek, DateTime? minDate, DateTime? maxDate, Func<DateTime, bool>? disabledDates,
        bool showOutsideDays, bool showWeekNumbers, bool disabled, Direction? dir,
        CalendarSelectionMode mode, int numberOfMonths, CalendarCaptionLayout captionLayout)
    {
        Scope = scope;
        VisibleMonth = visibleMonth;
        Selected = selected;
        Range = range;
        Multiple = multiple;
        Hover = hover;
        Focused = focused;
        Culture = culture;
        FirstDayOfWeek = firstDayOfWeek;
        MinDate = minDate;
        MaxDate = maxDate;
        DisabledDates = disabledDates;
        ShowOutsideDays = showOutsideDays;
        ShowWeekNumbers = showWeekNumbers;
        Disabled = disabled;
        Dir = dir;
        Mode = mode;
        NumberOfMonths = numberOfMonths;
        CaptionLayout = captionLayout;
    }

    internal CalendarScope Scope { get; }
    internal DateTime VisibleMonth { get; }
    internal DateTime? Selected { get; }
    internal CalendarRange? Range { get; }
    internal IReadOnlyList<DateTime>? Multiple { get; }
    internal DateTime? Hover { get; }
    internal DateTime Focused { get; }
    internal CultureInfo Culture { get; }
    internal DayOfWeek FirstDayOfWeek { get; }
    internal DateTime? MinDate { get; }
    internal DateTime? MaxDate { get; }
    internal Func<DateTime, bool>? DisabledDates { get; }
    internal bool ShowOutsideDays { get; }
    internal bool ShowWeekNumbers { get; }
    internal bool Disabled { get; }
    internal Direction? Dir { get; }
    internal CalendarSelectionMode Mode { get; }
    internal int NumberOfMonths { get; }
    internal CalendarCaptionLayout CaptionLayout { get; }

    internal bool IsDisabled(DateTime date)
    {
        var day = date.Date;
        if (MinDate is { } min && day < min) return true;
        if (MaxDate is { } max && day > max) return true;
        return DisabledDates?.Invoke(day) == true;
    }

    internal bool IsMultipleSelected(DateTime date)
    {
        if (Multiple is null) return false;
        foreach (var value in Multiple)
            if (value.Date == date) return true;
        return false;
    }

    /// <summary>Whether the roving date lies in one of the rendered months (one tab stop across all of them).</summary>
    internal bool FocusedInVisibleMonths
    {
        get
        {
            for (int index = 0; index < NumberOfMonths; index++)
            {
                var month = VisibleMonth.AddMonths(index);
                if (Focused.Year == month.Year && Focused.Month == month.Month) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The range the grid should paint: the committed endpoints, or while the first endpoint is pending, the span
    /// between that endpoint and the hovered day.
    /// </summary>
    internal CalendarRange? VisibleRange
    {
        get
        {
            if (Mode != CalendarSelectionMode.Range || Range is not { } range) return null;
            if (range.End is not null) return range;
            if (Hover is { } hover)
                return new CalendarRange(range.Start.Date, hover.Date).Order();
            return range;
        }
    }

    internal bool IsRangePreviewing =>
        Mode == CalendarSelectionMode.Range && Range is { } pending && pending.End is null && Hover is not null;
}

internal static class CalendarParts
{
    internal const string NavButtonClass =
        "inline-flex size-8 items-center justify-center rounded-md text-sm font-medium whitespace-nowrap transition-colors outline-none " +
        "hover:bg-accent hover:text-accent-foreground focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 " +
        "disabled:pointer-events-none disabled:opacity-50 aria-disabled:opacity-50";

    internal static string Caption(CalendarContext ctx) => Caption(ctx, ctx.VisibleMonth);

    internal static string Caption(CalendarContext ctx, DateTime month) =>
        ctx.Culture.DateTimeFormat.GetMonthName(month.Month) + " " + month.Year.ToString(ctx.Culture);

    internal static int WeekNumber(CalendarContext ctx, DateTime date) =>
        ctx.Culture.Calendar.GetWeekOfYear(date, ctx.Culture.DateTimeFormat.CalendarWeekRule, ctx.FirstDayOfWeek);

    internal static DateTime InitialFocus(CalendarContext ctx)
    {
        var first = new DateTime(ctx.VisibleMonth.Year, ctx.VisibleMonth.Month, 1);
        DateTime? preferred = ctx.Mode switch
        {
            CalendarSelectionMode.Range => ctx.Range?.Start,
            CalendarSelectionMode.Multiple => FirstMultipleInMonth(ctx, first),
            _ => ctx.Selected,
        };
        if (preferred is { } selected && selected.Year == first.Year && selected.Month == first.Month
            && !ctx.IsDisabled(selected))
            return selected.Date;
        return FirstEnabled(ctx) ?? first;
    }

    internal static DateTime? FirstEnabled(CalendarContext ctx) => FirstEnabled(ctx, ctx.VisibleMonth);

    internal static DateTime? FirstEnabled(CalendarContext ctx, DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        int days = DateTime.DaysInMonth(first.Year, first.Month);
        for (int day = 0; day < days; day++)
        {
            var date = first.AddDays(day);
            if (!ctx.IsDisabled(date)) return date;
        }
        return null;
    }

    static DateTime? FirstMultipleInMonth(CalendarContext ctx, DateTime month)
    {
        if (ctx.Multiple is null) return null;
        foreach (var value in ctx.Multiple)
            if (value.Year == month.Year && value.Month == month.Month) return value.Date;
        return null;
    }

    internal static void FocusIfRequested(CalendarContext ctx)
    {
        var scope = ctx.Scope;
        if (!scope.UserMovedFocus) return;
        scope.UserMovedFocus = false;
        if (scope.DayRefs.TryGetValue(ctx.Focused.Date, out var reference) && reference.Current is { } element)
            element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard, PreventScroll = true });
    }

    internal static UIButton NavButton(CalendarContext ctx, string? text, string slot, string label, Action action,
        bool atBoundary, SvgDocument? icon = null)
    {
        var button = new UIButton
        {
            Disabled = ctx.Disabled || IsAtBoundary(ctx, atBoundary),
            Children = new NodeList(),
        };
        TabsParts.Style(button, slot, NavButtonClass, null,
            new AttributeDictionary { ["aria-label"] = label });
        button.Attributes["type"] = "button";
        button.Children.Add(icon is null ? (Node)new UIText(text) : Glyph(icon, slot + "-icon"));
        button.Attributes["aria-disabled"] = TabsParts.Bool(button.Disabled);
        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (!e.DefaultPrevented) action();
        });
        return button;
    }

    static bool IsAtBoundary(CalendarContext ctx, bool atStart)
    {
        var month = new DateTime(ctx.VisibleMonth.Year, ctx.VisibleMonth.Month, 1);
        if (atStart)
            return ctx.MinDate is { } min && month <= new DateTime(min.Year, min.Month, 1);
        var lastMonth = month.AddMonths(Math.Max(1, ctx.NumberOfMonths) - 1);
        return ctx.MaxDate is { } max && lastMonth >= new DateTime(max.Year, max.Month, 1);
    }

    static UI.Svg Glyph(SvgDocument document, string slot)
    {
        var glyph = new UI.Svg(document) { Class = "size-4" };
        glyph.Attributes["aria-hidden"] = "true";
        glyph.Attributes["data-slot"] = slot;
        return glyph;
    }
}
