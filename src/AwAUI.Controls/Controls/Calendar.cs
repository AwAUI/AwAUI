using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>How <see cref="Calendar"/> turns a click on a day into a selection.</summary>
public enum CalendarSelectionMode : byte
{
    /// <summary>One date at a time.</summary>
    Single,

    /// <summary>A set of independent dates.</summary>
    Multiple,

    /// <summary>A contiguous range with two endpoints.</summary>
    Range,
}

/// <summary>A selected range. <see cref="End"/> is null while only the first endpoint has been chosen.</summary>
public readonly struct CalendarRange
{
    public CalendarRange(DateTime start, DateTime? end = null)
    {
        Start = start.Date;
        End = end?.Date;
    }

    /// <summary>The first endpoint.</summary>
    public DateTime Start { get; }

    /// <summary>The second endpoint, or null while a range is in progress.</summary>
    public DateTime? End { get; }

    /// <summary>Whether both endpoints have been chosen.</summary>
    public bool IsComplete => End.HasValue;

    /// <summary>Returns the endpoints in ascending order.</summary>
    public CalendarRange Order()
    {
        if (End is not { } end || end >= Start) return this;
        return new CalendarRange(end, Start);
    }

    /// <summary>Whether <paramref name="date"/> lies between the endpoints (inclusive).</summary>
    public bool Contains(DateTime date)
    {
        var ordered = Order();
        if (ordered.End is not { } end) return date.Date == ordered.Start;
        return date.Date >= ordered.Start && date.Date <= end;
    }

    public override string ToString() =>
        End is { } end ? Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".." + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// A six-week month grid for choosing dates. The visible month is controlled through <see cref="DisplayDate"/> (or
/// starts at <see cref="DefaultDisplayDate"/>), selection through <see cref="SelectedDate"/> (single),
/// <see cref="SelectedDates"/> (multiple), or <see cref="RangeStart"/> / <see cref="RangeEnd"/> (range). Every mode can
/// use a <c>Default…</c> value for uncontrolled use.
/// </summary>
/// <remarks>
/// Days outside the displayed month are rendered in the fixed 42-cell grid and are still selectable. Days outside
/// <see cref="DisplayDateStart"/> / <see cref="DisplayDateEnd"/> or <see cref="MinYear"/> / <see cref="MaxYear"/> are
/// disabled through <c>aria-disabled</c>. The keyboard follows a single roving tab stop: arrows move by day and week,
/// Home/End move to the week edges, PageUp/PageDown move by month, Shift+PageUp/PageDown by year, and Enter or Space
/// activates the focused day. Today is marked with <c>data-today</c> unless <see cref="IsTodayHighlighted"/> is false.
/// </remarks>
public sealed partial class Calendar : Control
{
    readonly ControllableState<DateTime?> _selectedDate = new();
    readonly ControllableState<IReadOnlyList<DateTime>> _selectedDates = new(DateTimeListComparer.Instance);
    readonly ControllableState<CalendarRange?> _range = new();
    readonly ControllableState<DateTime> _displayDate = new();
    readonly Dictionary<DateTime, Ref<Element>> _dayRefs = new();
    State<DateTime>? _focused;
    bool _focusRequested;
    CultureInfo _culture = CultureInfo.CurrentCulture;

    protected override string GlobalStyleSheet => CalendarStyles.StyleSheet;

    /// <summary>Selection model. Defaults to <see cref="CalendarSelectionMode.Single"/>.</summary>
    public CalendarSelectionMode SelectionMode { get; set; } = CalendarSelectionMode.Single;

    // ---------------------------------------------------------------- 单选

    /// <summary>Controlled selected date. Null means no selection unless <see cref="IsSelectedDateControlled"/> is set.</summary>
    public DateTime? SelectedDate { get; set; }

    /// <summary>Initial uncontrolled selection.</summary>
    public DateTime? DefaultSelectedDate { get; set; }

    /// <summary>Treats a null <see cref="SelectedDate"/> as a controlled empty selection.</summary>
    public bool IsSelectedDateControlled { get; set; }

    /// <summary>Reports a selection change. Supports <c>@bind-SelectedDate</c>.</summary>
    public Action<DateTime?>? SelectedDateChanged { get; set; }

    // ---------------------------------------------------------------- 多选

    /// <summary>Controlled selected dates for <see cref="CalendarSelectionMode.Multiple"/>.</summary>
    public IReadOnlyList<DateTime>? SelectedDates { get; set; }

    /// <summary>Initial uncontrolled multiple selection.</summary>
    public IReadOnlyList<DateTime>? DefaultSelectedDates { get; set; }

    /// <summary>Treats a null <see cref="SelectedDates"/> as a controlled empty selection.</summary>
    public bool IsSelectedDatesControlled { get; set; }

    /// <summary>Reports a change to multiple selection. The list is a new, sorted instance.</summary>
    public Action<IReadOnlyList<DateTime>>? SelectedDatesChanged { get; set; }

    // ---------------------------------------------------------------- 范围

    /// <summary>Controlled first endpoint of the selected range.</summary>
    public DateTime? RangeStart { get; set; }

    /// <summary>Controlled second endpoint; null while the range is in progress.</summary>
    public DateTime? RangeEnd { get; set; }

    public DateTime? DefaultRangeStart { get; set; }
    public DateTime? DefaultRangeEnd { get; set; }

    /// <summary>Treats both range endpoints being null as a controlled empty range.</summary>
    public bool IsRangeControlled { get; set; }

    /// <summary>Reports a range change. Supports <c>@bind-RangeStart</c> style callbacks.</summary>
    public Action<CalendarRange?>? RangeChanged { get; set; }

    // ---------------------------------------------------------------- 显示

    /// <summary>Controlled first day of the displayed month.</summary>
    public DateTime? DisplayDate { get; set; }

    /// <summary>Initial visible month when <see cref="DisplayDate"/> is null.</summary>
    public DateTime? DefaultDisplayDate { get; set; }

    /// <summary>Reports a visible-month change.</summary>
    public Action<DateTime>? DisplayDateChanged { get; set; }

    /// <summary>The earliest selectable date, inclusive.</summary>
    public DateTime? DisplayDateStart { get; set; }

    /// <summary>The latest selectable date, inclusive.</summary>
    public DateTime? DisplayDateEnd { get; set; }

    /// <summary>Lower year bound used when <see cref="DisplayDateStart"/> is absent. Defaults to 1900.</summary>
    public int MinYear { get; set; } = 1900;

    /// <summary>Upper year bound used when <see cref="DisplayDateEnd"/> is absent. Defaults to 2100.</summary>
    public int MaxYear { get; set; } = 2100;

    /// <summary>Marks today with <c>data-today</c> and <c>aria-current="date"</c>. Defaults to true.</summary>
    public bool IsTodayHighlighted { get; set; } = true;

    /// <summary>First day of the week; null uses <see cref="Culture"/>.</summary>
    public DayOfWeek? FirstDayOfWeek { get; set; }

    /// <summary>
    /// Culture used for month, weekday, and day names and for display formatting. Defaults to
    /// <see cref="CultureInfo.CurrentCulture"/>; assigning null restores the current culture.
    /// </summary>
    public CultureInfo Culture
    {
        get => _culture;
        set => _culture = value ?? CultureInfo.CurrentCulture;
    }

    public bool IsEnabled { get; set; } = true;

    protected override Node Build()
    {
        var culture = Culture;
        var firstDay = CalendarMath.ResolveFirstDayOfWeek(culture, FirstDayOfWeek);
        DateTime minDate;
        DateTime maxDate;
        GetBounds(out minDate, out maxDate);

        DateTime? selected = _selectedDate.Sync(IsSelectedDateControlled || SelectedDate.HasValue,
            SelectedDate.GetValueOrDefault(), DefaultSelectedDate.GetValueOrDefault(), SelectedDateChanged);
        IReadOnlyList<DateTime> multiple = _selectedDates.Sync(IsSelectedDatesControlled || SelectedDates is not null,
            SelectedDates ?? Array.Empty<DateTime>(), DefaultSelectedDates ?? Array.Empty<DateTime>(), SelectedDatesChanged);
        var defaultRange = DefaultRangeStart.HasValue ? new CalendarRange(DefaultRangeStart.Value, DefaultRangeEnd) : (CalendarRange?)null;
        var controlledRange = RangeStart.HasValue || RangeEnd.HasValue ? new CalendarRange(RangeStart!.Value, RangeEnd) : (CalendarRange?)null;
        var range = _range.Sync(IsRangeControlled || controlledRange.HasValue, controlledRange, defaultRange, RangeChanged);

        DateTime fallbackDisplay = DefaultDisplayDate ?? selected ?? range?.Start ?? FirstMultiple(multiple) ?? DateTime.Today;
        DateTime display = _displayDate.Sync(DisplayDate, fallbackDisplay, DisplayDateChanged);
        DateTime visible = CalendarMath.ClampMonth(display, minDate, maxDate);
        DateTime monthStart = CalendarMath.MonthStart(visible);

        if (_focused is null) _focused = new State<DateTime>(InitialFocus(monthStart, selected, range, multiple, minDate, maxDate));
        DateTime focused = _focused.Value;
        bool focusedInMonth = focused.Year == monthStart.Year && focused.Month == monthStart.Month && IsEnabledDate(focused, minDate, maxDate);
        DateTime tabStop = focusedInMonth ? focused.Date : FirstEnabledInMonth(monthStart, minDate, maxDate);
        if (tabStop == default) tabStop = monthStart;

        var root = Apply(new Div { Role = Role.Group }, "aw-calendar");
        root.AriaLabel = AriaLabel ?? "Calendar";
        root.Attributes["data-selection-mode"] = SelectionMode switch
        {
            CalendarSelectionMode.Multiple => "multiple",
            CalendarSelectionMode.Range => "range",
            _ => "single",
        };
        root.Attributes["aria-disabled"] = PartProps.Bool(!IsEnabled);

        var caption = monthStart.ToString("MMMM yyyy", culture);
        var captionNode = new Div { Children = new NodeList { new UIText(caption) }, AriaLabel = caption };
        captionNode.Attributes["part"] = "caption";
        captionNode.Attributes["aria-live"] = "polite";
        var header = new Div { Children = new NodeList
        {
            NavigationButton("previous-year", "Previous year", "«",
                CalendarMath.MonthStart(CalendarMath.AddMonthsClamped(monthStart, -12)) < CalendarMath.MonthStart(minDate),
                () => ShowMonth(CalendarMath.AddMonthsClamped(monthStart, -12), minDate, maxDate, false)),
            NavigationButton("previous-month", "Previous month", "‹",
                monthStart <= CalendarMath.MonthStart(minDate),
                () => ShowMonth(CalendarMath.AddMonthsClamped(monthStart, -1), minDate, maxDate, false)),
            captionNode,
            NavigationButton("next-month", "Next month", "›",
                monthStart >= CalendarMath.MonthStart(maxDate),
                () => ShowMonth(CalendarMath.AddMonthsClamped(monthStart, 1), minDate, maxDate, false)),
            NavigationButton("next-year", "Next year", "»",
                CalendarMath.MonthStart(CalendarMath.AddMonthsClamped(monthStart, 12)) > CalendarMath.MonthStart(maxDate),
                () => ShowMonth(CalendarMath.AddMonthsClamped(monthStart, 12), minDate, maxDate, false)),
        } };
        header.Attributes["part"] = "header";

        var grid = new Div { Role = Role.Grid, AriaLabel = caption };
        grid.Attributes["part"] = "grid";
        grid.Children.Add(BuildWeekdayHeader(culture, firstDay));
        var layout = CalendarMath.LayoutMonth(monthStart, firstDay);
        _dayRefs.Clear();
        for (int week = 0; week < CalendarMath.WeeksPerMonth; week++)
        {
            var row = new Div { Role = Role.Row };
            row.Attributes["part"] = "week";
            for (int day = 0; day < 7; day++)
            {
                var date = layout.Days[week * 7 + day];
                row.Children.Add(BuildDay(date, monthStart, tabStop, selected, multiple, range, minDate, maxDate, culture));
            }
            grid.Children.Add(row);
        }

        root.Children.Add(header);
        root.Children.Add(grid);

        var requested = _focusRequested;
        _focusRequested = false;
        UseEffect(() =>
        {
            if (requested && _dayRefs.TryGetValue(focused.Date, out var reference) && reference.Current is { } element)
                element.Focus();
            return null;
        }, focused, requested);

        return root;
    }

    UIButton BuildDay(DateTime date, DateTime monthStart, DateTime tabStop, DateTime? selected,
        IReadOnlyList<DateTime> multiple, CalendarRange? range, DateTime minDate, DateTime maxDate, CultureInfo culture)
    {
        var day = date.Date;
        bool disabled = !IsEnabled || !IsEnabledDate(day, minDate, maxDate);
        bool outside = day.Year != monthStart.Year || day.Month != monthStart.Month;
        bool today = IsTodayHighlighted && day == DateTime.Today;
        bool isSelected = SelectionMode switch
        {
            CalendarSelectionMode.Multiple => ContainsDay(multiple, day),
            CalendarSelectionMode.Range => range is { } current && current.Contains(day),
            _ => selected is { } value && value.Date == day,
        };

        var button = new UIButton
        {
            Role = Role.Cell,
            Disabled = disabled,
            TabIndex = !disabled && day == tabStop.Date ? 0 : -1,
            AriaLabel = day.ToString("D", culture),
            Children = new NodeList { new UIText(day.Day.ToString(culture)) },
        };
        button.Attributes["type"] = "button";
        button.Attributes["part"] = "day";
        button.Attributes["data-date"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        button.Attributes["data-day"] = day.Day.ToString(CultureInfo.InvariantCulture);
        button.Attributes["data-selected"] = PartProps.Bool(isSelected);
        button.Attributes["data-today"] = today ? "" : null;
        button.Attributes["data-outside"] = outside ? "" : null;
        button.Attributes["data-disabled"] = disabled ? "" : null;
        button.Attributes["aria-selected"] = PartProps.Bool(isSelected);
        button.Attributes["aria-disabled"] = PartProps.Bool(disabled);
        button.Attributes["aria-current"] = today ? "date" : null;

        var reference = new Ref<Element>();
        _dayRefs[day] = reference;
        button.Ref = reference;

        button.OnClick = e =>
        {
            if (e.DefaultPrevented || disabled) return;
            Activate(day, monthStart, minDate, maxDate);
        };
        button.OnKeyDown = e => OnDayKey(e, day, monthStart, minDate, maxDate);
        return button;
    }

    static Div BuildWeekdayHeader(CultureInfo culture, DayOfWeek firstDay)
    {
        var row = new Div { Role = Role.Row };
        row.Attributes["part"] = "week";
        for (int index = 0; index < 7; index++)
        {
            var dayOfWeek = (DayOfWeek)(((int)firstDay + index) % 7);
            var cell = new Div
            {
                Role = Role.Cell,
                AriaLabel = culture.DateTimeFormat.GetDayName(dayOfWeek),
                Children = new NodeList { new UIText(culture.DateTimeFormat.ShortestDayNames[(int)dayOfWeek]) },
            };
            cell.Attributes["part"] = "weekday";
            row.Children.Add(cell);
        }
        return row;
    }

    UIButton NavigationButton(string part, string label, string text, bool atBoundary, Action action)
    {
        var button = new UIButton
        {
            Disabled = atBoundary || !IsEnabled,
            AriaLabel = label,
            Children = new NodeList { new UIText(text) },
        };
        button.Attributes["type"] = "button";
        button.Attributes["part"] = part;
        button.OnClick = e =>
        {
            if (!e.DefaultPrevented) action();
        };
        return button;
    }

    void Activate(DateTime day, DateTime monthStart, DateTime minDate, DateTime maxDate)
    {
        if (!IsEnabled || !IsEnabledDate(day, minDate, maxDate)) return;
        day = day.Date;

        switch (SelectionMode)
        {
            case CalendarSelectionMode.Multiple:
                var next = new List<DateTime>();
                bool removed = false;
                for (int i = 0; i < _selectedDates.Value.Count; i++)
                {
                    var existing = _selectedDates.Value[i].Date;
                    if (existing == day) { removed = true; continue; }
                    next.Add(existing);
                }
                if (!removed) next.Add(day);
                next.Sort();
                _selectedDates.Set(next);
                break;
            case CalendarSelectionMode.Range:
                var current = _range.Value;
                if (current is not { } pending || pending.End is not null)
                    _range.Set(new CalendarRange(day));
                else
                {
                    var start = pending.Start.Date;
                    _range.Set(day < start ? new CalendarRange(day, start) : new CalendarRange(start, day));
                }
                break;
            default:
                _selectedDate.Set(day);
                break;
        }

        if (day.Year != monthStart.Year || day.Month != monthStart.Month)
            ShowMonth(new DateTime(day.Year, day.Month, 1), minDate, maxDate, false);
        SetFocus(day, true);
    }

    void OnDayKey(KeyEvent e, DateTime day, DateTime monthStart, DateTime minDate, DateTime maxDate)
    {
        if (e.DefaultPrevented || !IsEnabled) return;
        if ((e.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        DateTime target;
        switch (e.Key)
        {
            case "ArrowLeft": target = CalendarMath.AddDaysClamped(day, -1); break;
            case "ArrowRight": target = CalendarMath.AddDaysClamped(day, 1); break;
            case "ArrowUp": target = CalendarMath.AddDaysClamped(day, -7); break;
            case "ArrowDown": target = CalendarMath.AddDaysClamped(day, 7); break;
            case "Home": target = CalendarMath.StartOfWeek(day, CalendarMath.ResolveFirstDayOfWeek(Culture, FirstDayOfWeek)); break;
            case "End": target = CalendarMath.AddDaysClamped(CalendarMath.StartOfWeek(day, CalendarMath.ResolveFirstDayOfWeek(Culture, FirstDayOfWeek)), 6); break;
            case "PageUp": target = CalendarMath.AddMonthsClamped(day, shift ? -12 : -1); break;
            case "PageDown": target = CalendarMath.AddMonthsClamped(day, shift ? 12 : 1); break;
            default: return;
        }
        e.PreventDefault();

        int direction = target.Date >= day.Date ? 1 : -1;
        target = CalendarMath.ClampDate(target, minDate, maxDate);
        for (int i = 0; i < 400 && !IsEnabledDate(target, minDate, maxDate); i++)
            target = target.AddDays(direction);
        if (!IsEnabledDate(target, minDate, maxDate)) return;

        if (target.Year != monthStart.Year || target.Month != monthStart.Month)
            ShowMonth(new DateTime(target.Year, target.Month, 1), minDate, maxDate, false);
        SetFocus(target, true);
    }

    void ShowMonth(DateTime month, DateTime minDate, DateTime maxDate, bool requestFocus)
    {
        var target = CalendarMath.ClampMonth(month, minDate, maxDate);
        _displayDate.Set(target);
        if (_focused is null) return;
        var focused = _focused.Peek();
        if (focused.Year == target.Year && focused.Month == target.Month) return;
        int day = CalendarMath.ClampDay(focused.Day, target.Year, target.Month);
        _focused.Value = new DateTime(target.Year, target.Month, day);
        if (requestFocus) _focusRequested = true;
    }

    void SetFocus(DateTime day, bool requestFocus)
    {
        day = day.Date;
        if (_focused is null) _focused = new State<DateTime>(day);
        else _focused.Value = day;
        if (requestFocus) _focusRequested = true;
    }

    DateTime InitialFocus(DateTime monthStart, DateTime? selected, CalendarRange? range,
        IReadOnlyList<DateTime> multiple, DateTime minDate, DateTime maxDate)
    {
        DateTime? preferred = SelectionMode switch
        {
            CalendarSelectionMode.Range => range?.Start,
            CalendarSelectionMode.Multiple => FirstInMonth(multiple, monthStart),
            _ => selected,
        };
        if (preferred is { } value && value.Year == monthStart.Year && value.Month == monthStart.Month
            && IsEnabledDate(value, minDate, maxDate))
            return value.Date;
        return FirstEnabledInMonth(monthStart, minDate, maxDate);
    }

    DateTime FirstEnabledInMonth(DateTime month, DateTime minDate, DateTime maxDate)
    {
        var first = CalendarMath.MonthStart(month);
        int days = DateTime.DaysInMonth(first.Year, first.Month);
        for (int index = 0; index < days; index++)
        {
            var date = first.AddDays(index);
            if (IsEnabledDate(date, minDate, maxDate)) return date;
        }
        return first;
    }

    bool IsEnabledDate(DateTime day, DateTime minDate, DateTime maxDate) =>
        IsEnabled && day.Date >= minDate.Date && day.Date <= maxDate.Date;

    void GetBounds(out DateTime minDate, out DateTime maxDate)
    {
        int lowerYear = CalendarMath.ClampYear(Math.Min(MinYear, MaxYear), 1, 9999);
        int upperYear = CalendarMath.ClampYear(Math.Max(MinYear, MaxYear), 1, 9999);
        minDate = new DateTime(lowerYear, 1, 1);
        maxDate = new DateTime(upperYear, 12, 31);
        if (DisplayDateStart is { } start && start.Date > minDate) minDate = start.Date;
        if (DisplayDateEnd is { } end && end.Date < maxDate) maxDate = end.Date;
        if (maxDate < minDate)
        {
            var swap = minDate;
            minDate = maxDate;
            maxDate = swap;
        }
    }

    static DateTime? FirstMultiple(IReadOnlyList<DateTime> values)
    {
        DateTime? first = null;
        for (int i = 0; i < values.Count; i++)
            if (first is null || values[i].Date < first.Value.Date) first = values[i].Date;
        return first;
    }

    static DateTime? FirstInMonth(IReadOnlyList<DateTime> values, DateTime month)
    {
        DateTime? first = null;
        for (int i = 0; i < values.Count; i++)
        {
            var value = values[i].Date;
            if (value.Year != month.Year || value.Month != month.Month) continue;
            if (first is null || value < first.Value) first = value;
        }
        return first;
    }

    static bool ContainsDay(IReadOnlyList<DateTime> values, DateTime day)
    {
        for (int i = 0; i < values.Count; i++)
            if (values[i].Date == day) return true;
        return false;
    }

    sealed class DateTimeListComparer : IEqualityComparer<IReadOnlyList<DateTime>>
    {
        public static readonly DateTimeListComparer Instance = new DateTimeListComparer();

        public bool Equals(IReadOnlyList<DateTime>? x, IReadOnlyList<DateTime>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; i++)
                if (x[i].Date != y[i].Date) return false;
            return true;
        }

        public int GetHashCode(IReadOnlyList<DateTime> obj)
        {
            int hash = 17;
            for (int i = 0; i < obj.Count; i++) hash = hash * 31 + obj[i].Date.GetHashCode();
            return hash;
        }
    }
}

static class CalendarStyles
{
    internal const string StyleSheet = """
        @layer aw-calendar-defaults {
          :where(.aw-calendar) {
            display: inline-flex;
            flex-direction: column;
            gap: 4px;
            min-width: 0;
          }
          :where(.aw-calendar [part="header"]) {
            display: flex;
            flex-direction: row;
            align-items: center;
            gap: 4px;
          }
          :where(.aw-calendar [part="caption"]) {
            flex: 1 1 auto;
            text-align: center;
            white-space: nowrap;
          }
          :where(.aw-calendar [part="previous-year"], .aw-calendar [part="previous-month"],
                 .aw-calendar [part="next-month"], .aw-calendar [part="next-year"]) {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            min-width: 24px;
            min-height: 24px;
            padding: 0 4px;
          }
          :where(.aw-calendar [part="grid"]) {
            display: grid;
            grid-template-columns: repeat(7, 1fr);
            gap: 2px;
          }
          :where(.aw-calendar [part="week"]) {
            display: contents;
          }
          :where(.aw-calendar [part="weekday"]) {
            display: flex;
            align-items: center;
            justify-content: center;
            min-width: 28px;
            min-height: 24px;
            font-size: 0.8em;
            opacity: 0.7;
          }
          :where(.aw-calendar [part="day"]) {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            min-width: 28px;
            min-height: 28px;
            padding: 0;
            border: 0;
            background: transparent;
            font: inherit;
          }
          :where(.aw-calendar [part="day"][data-outside]) {
            opacity: 0.45;
          }
          :where(.aw-calendar [part="day"][data-today]) {
            outline: 1px solid currentColor;
            outline-offset: -1px;
            border-radius: 4px;
          }
          :where(.aw-calendar [part="day"][aria-selected="true"]) {
            background: color-mix(in srgb, currentColor 16%, transparent);
            border-radius: 4px;
          }
          :where(.aw-calendar [part="day"][aria-disabled="true"]) {
            opacity: 0.35;
          }
        }
        """;
}
