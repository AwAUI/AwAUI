using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>
/// A date chooser with independent year, month, and day lists (Avalonia's <c>DatePicker</c> shape). Each column can be
/// hidden with <see cref="YearVisible"/>, <see cref="MonthVisible"/>, and <see cref="DayVisible"/>. Changing a column
/// keeps the other components and clamps the day when the target month is shorter (31 March → 30 April). The lists use
/// the listbox/option roles; arrow keys, Home, and End move the selection within a column, and Left/Right move to the
/// neighbouring column.
/// </summary>
public sealed partial class DatePicker : Control
{
    readonly ControllableState<DateTime?> _selected = new();
    readonly Dictionary<string, Ref<Element>> _refs = new();
    readonly Dictionary<string, int> _tabStops = new();
    CultureInfo _culture = CultureInfo.CurrentCulture;
    string? _focusKey;
    bool _focusRequested;

    protected override string GlobalStyleSheet => DatePickerStyles.StyleSheet;

    /// <summary>Controlled selected date; null means no date unless <see cref="IsSelectedDateControlled"/> is set.</summary>
    public DateTime? SelectedDate { get; set; }

    /// <summary>Initial uncontrolled date. When null the control starts from today.</summary>
    public DateTime? DefaultSelectedDate { get; set; }

    /// <summary>Treats a null <see cref="SelectedDate"/> as a controlled empty selection.</summary>
    public bool IsSelectedDateControlled { get; set; }

    /// <summary>Reports a selection change. Supports <c>@bind-SelectedDate</c>.</summary>
    public Action<DateTime?>? SelectedDateChanged { get; set; }

    /// <summary>Lowest selectable year. Defaults to 1900.</summary>
    public int MinYear { get; set; } = 1900;

    /// <summary>Highest selectable year. Defaults to 2100.</summary>
    public int MaxYear { get; set; } = 2100;

    public bool DayVisible { get; set; } = true;
    public bool MonthVisible { get; set; } = true;
    public bool YearVisible { get; set; } = true;

    /// <summary>
    /// Culture used for month and day names and for numeric labels. Defaults to
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
        var selected = _selected.Sync(IsSelectedDateControlled || SelectedDate.HasValue,
            SelectedDate.GetValueOrDefault(), DefaultSelectedDate.GetValueOrDefault(), SelectedDateChanged);
        int minYear = Math.Max(1, Math.Min(MinYear, MaxYear));
        int maxYear = Math.Min(9999, Math.Max(MinYear, MaxYear));
        var minDate = new DateTime(minYear, 1, 1);
        var maxDate = new DateTime(maxYear, 12, 31);
        DateTime display = CalendarMath.ClampDate(selected ?? DefaultSelectedDate ?? DateTime.Today, minDate, maxDate);
        int year = CalendarMath.ClampYear(display.Year, minYear, maxYear);
        int month = display.Month;
        int day = CalendarMath.ClampDay(display.Day, year, month);

        var root = Apply(new Div { Role = Role.Group }, "aw-date-picker");
        root.AriaLabel = AriaLabel ?? "Date";
        root.Attributes["aria-disabled"] = PartProps.Bool(!IsEnabled);
        root.Attributes["data-disabled"] = IsEnabled ? null : "";

        _refs.Clear();
        _tabStops.Clear();

        if (YearVisible)
        {
            var values = new List<int>();
            for (int value = minYear; value <= maxYear; value++) values.Add(value);
            _tabStops["year"] = year;
            root.Children.Add(BuildColumn("year", "Year", values, year, culture,
                value => value.ToString(culture),
                (value, focus) => CommitSelection(value, month, day, "year", value, focus)));
        }

        if (MonthVisible)
        {
            var values = new List<int>();
            for (int value = 1; value <= 12; value++) values.Add(value);
            _tabStops["month"] = month;
            root.Children.Add(BuildColumn("month", "Month", values, month, culture,
                value => culture.DateTimeFormat.GetMonthName(value),
                (value, focus) => CommitSelection(year, value, day, "month", value, focus)));
        }

        if (DayVisible)
        {
            var values = new List<int>();
            int days = DateTime.DaysInMonth(year, month);
            for (int value = 1; value <= days; value++) values.Add(value);
            _tabStops["day"] = day;
            root.Children.Add(BuildColumn("day", "Day", values, day, culture,
                value => value.ToString(culture),
                (value, focus) => CommitSelection(year, month, value, "day", value, focus)));
        }

        var focusKey = _focusKey;
        bool requested = _focusRequested;
        _focusRequested = false;
        UseEffect(() =>
        {
            if (requested && focusKey is not null && _refs.TryGetValue(focusKey, out var reference) && reference.Current is { } element)
                element.Focus();
            return null;
        }, focusKey, requested);

        return root;
    }

    Div BuildColumn(string column, string label, IReadOnlyList<int> values, int selectedValue,
        CultureInfo culture, Func<int, string> text, Action<int, bool> choose)
    {
        var list = new Div { Role = Role.ListBox, AriaLabel = label };
        list.Attributes["part"] = column;
        list.Attributes["data-column"] = column;

        for (int index = 0; index < values.Count; index++)
        {
            int value = values[index];
            bool isSelected = value == selectedValue;
            var button = new UIButton
            {
                Role = Role.Option,
                Disabled = !IsEnabled,
                TabIndex = isSelected ? 0 : -1,
                AriaLabel = text(value),
                Children = new NodeList { new UIText(text(value)) },
            };
            button.Attributes["type"] = "button";
            button.Attributes["part"] = "option";
            button.Attributes["data-column"] = column;
            button.Attributes["data-value"] = value.ToString(CultureInfo.InvariantCulture);
            button.Attributes["aria-selected"] = PartProps.Bool(isSelected);
            button.Attributes["data-selected"] = isSelected ? "" : null;

            var reference = new Ref<Element>();
            _refs[column + ":" + value.ToString(CultureInfo.InvariantCulture)] = reference;
            button.Ref = reference;

            int capturedIndex = index;
            button.OnClick = e =>
            {
                if (!e.DefaultPrevented && IsEnabled) choose(value, false);
            };
            button.OnKeyDown = e => OnOptionKey(e, column, values, capturedIndex, choose);
            list.Children.Add(button);
        }
        return list;
    }

    void OnOptionKey(KeyEvent e, string column, IReadOnlyList<int> values, int index, Action<int, bool> choose)
    {
        if (e.DefaultPrevented || !IsEnabled) return;
        if ((e.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        int next;
        switch (e.Key)
        {
            case "ArrowUp": next = index - 1; break;
            case "ArrowDown": next = index + 1; break;
            case "Home": next = 0; break;
            case "End": next = values.Count - 1; break;
            case "ArrowLeft":
            case "ArrowRight":
                e.PreventDefault();
                FocusNeighbour(column, e.Key == "ArrowRight" ? 1 : -1);
                return;
            default: return;
        }
        e.PreventDefault();
        if (next < 0) next = 0;
        if (next >= values.Count) next = values.Count - 1;
        choose(values[next], true);
    }

    void FocusNeighbour(string column, int direction)
    {
        string? target = null;
        if (column == "year") target = direction > 0 && MonthVisible ? "month" : null;
        else if (column == "month")
            target = direction > 0 ? (DayVisible ? "day" : null) : (YearVisible ? "year" : null);
        else if (column == "day") target = direction < 0 && MonthVisible ? "month" : null;
        if (target is null || !_tabStops.TryGetValue(target, out int value)) return;

        _focusKey = target + ":" + value.ToString(CultureInfo.InvariantCulture);
        _focusRequested = true;
        Invalidate();
    }

    void CommitSelection(int year, int month, int day, string focusColumn, int focusValue, bool focus)
    {
        int clampedDay = CalendarMath.ClampDay(day, year, month);
        var date = new DateTime(year, month, clampedDay);
        if (focus)
        {
            int tabValue = focusColumn == "day" ? clampedDay : focusColumn == "month" ? month : focusValue;
            _focusKey = focusColumn + ":" + tabValue.ToString(CultureInfo.InvariantCulture);
            _focusRequested = true;
        }
        _selected.Set(date);
        Invalidate();
    }
}

static class DatePickerStyles
{
    internal const string StyleSheet = """
        @layer aw-date-picker-defaults {
          :where(.aw-date-picker) {
            display: inline-flex;
            flex-direction: row;
            align-items: stretch;
            gap: 2px;
            min-width: 0;
          }
          :where(.aw-date-picker > [part="year"],
                 .aw-date-picker > [part="month"],
                 .aw-date-picker > [part="day"]) {
            display: flex;
            flex-direction: column;
            overflow: auto;
            max-height: 220px;
            min-width: 64px;
          }
          :where(.aw-date-picker [part="option"]) {
            display: flex;
            align-items: center;
            justify-content: flex-start;
            min-height: 24px;
            padding: 0 6px;
            border: 0;
            background: transparent;
            font: inherit;
            text-align: start;
          }
          :where(.aw-date-picker [part="option"][data-selected]) {
            background: color-mix(in srgb, currentColor 16%, transparent);
          }
        }
        """;
}
