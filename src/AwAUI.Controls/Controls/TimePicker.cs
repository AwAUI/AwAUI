using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>The supported <see cref="TimePicker.ClockIdentifier"/> values (Avalonia-compatible strings).</summary>
public static class ClockIdentifiers
{
    /// <summary>Hours 1–12 with an AM/PM column.</summary>
    public const string TwelveHourClock = "12HourClock";

    /// <summary>Hours 0–23.</summary>
    public const string TwentyFourHourClock = "24HourClock";
}

/// <summary>
/// A time chooser with hour, minute, optional second, and (for the 12-hour clock) AM/PM lists. Values are discrete, so
/// the control uses the listbox/option roles rather than a spinbutton: each column is a <c>listbox</c> and every value is
/// an <c>option</c> with <c>aria-selected</c>. Arrow keys, Home, and End move within a column; Left/Right move between
/// columns. <see cref="MinuteIncrement"/> and <see cref="SecondIncrement"/> choose the offered steps; the current value
/// is always included even when it does not align with the step.
/// </summary>
public sealed partial class TimePicker : Control
{
    readonly ControllableState<TimeSpan?> _selected = new();
    readonly Dictionary<string, Ref<Element>> _refs = new();
    readonly Dictionary<string, string> _tabStops = new();
    CultureInfo _culture = CultureInfo.CurrentCulture;
    string? _focusKey;
    bool _focusRequested;

    protected override string GlobalStyleSheet => TimePickerStyles.StyleSheet;

    /// <summary>Controlled selected time; null means no time unless <see cref="IsSelectedTimeControlled"/> is set.</summary>
    public TimeSpan? SelectedTime { get; set; }

    /// <summary>Initial uncontrolled time. When null the control starts at 00:00.</summary>
    public TimeSpan? DefaultSelectedTime { get; set; }

    /// <summary>Treats a null <see cref="SelectedTime"/> as a controlled empty selection.</summary>
    public bool IsSelectedTimeControlled { get; set; }

    /// <summary>Reports a selection change. Supports <c>@bind-SelectedTime</c>.</summary>
    public Action<TimeSpan?>? SelectedTimeChanged { get; set; }

    /// <summary>
    /// Clock style: <see cref="ClockIdentifiers.TwelveHourClock"/> or
    /// <see cref="ClockIdentifiers.TwentyFourHourClock"/>. Defaults to the 24-hour clock.
    /// </summary>
    public string ClockIdentifier { get; set; } = ClockIdentifiers.TwentyFourHourClock;

    /// <summary>Minute step offered by the minute column, from 1 to 60. Defaults to 1.</summary>
    public int MinuteIncrement { get; set; } = 1;

    /// <summary>Second step offered when <see cref="ShowSeconds"/> is true, from 1 to 60. Defaults to 1.</summary>
    public int SecondIncrement { get; set; } = 1;

    /// <summary>Shows the seconds column. Defaults to false.</summary>
    public bool ShowSeconds { get; set; }

    /// <summary>
    /// Culture used for AM/PM designators and numeric labels. Defaults to <see cref="CultureInfo.CurrentCulture"/>;
    /// assigning null restores the current culture.
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
        var selected = _selected.Sync(IsSelectedTimeControlled || SelectedTime.HasValue,
            SelectedTime.GetValueOrDefault(), DefaultSelectedTime.GetValueOrDefault(), SelectedTimeChanged);
        var time = TimeMath.Normalize(selected ?? DefaultSelectedTime ?? TimeSpan.Zero);
        int hour24, minute, second;
        TimeMath.Split(time, out hour24, out minute, out second);
        bool twelveHour = IsTwelveHourClock(ClockIdentifier);
        bool isPm = false;
        int displayHour = twelveHour ? TimeMath.ToTwelveHour(hour24, out isPm) : hour24;

        var root = Apply(new Div { Role = Role.Group }, "aw-time-picker");
        root.AriaLabel = AriaLabel ?? "Time";
        root.Attributes["aria-disabled"] = PartProps.Bool(!IsEnabled);
        root.Attributes["data-disabled"] = IsEnabled ? null : "";
        root.Attributes["data-clock"] = twelveHour ? "12" : "24";

        _refs.Clear();
        _tabStops.Clear();

        var hours = new List<int>();
        if (twelveHour)
        {
            for (int value = 1; value <= 12; value++) hours.Add(value);
        }
        else
        {
            for (int value = 0; value <= 23; value++) hours.Add(value);
        }
        _tabStops["hour"] = displayHour.ToString(CultureInfo.InvariantCulture);
        root.Children.Add(BuildColumn("hour", "Hour", hours, displayHour, culture,
            value => value.ToString(culture),
            (value, focus) => Commit(
                twelveHour ? TimeMath.ToTwentyFourHour(value, isPm) : value,
                minute, second, "hour", value.ToString(CultureInfo.InvariantCulture), focus)));

        var minutes = WithCurrent(TimeMath.MinuteValues(MinuteIncrement), minute);
        _tabStops["minute"] = minute.ToString(CultureInfo.InvariantCulture);
        root.Children.Add(BuildColumn("minute", "Minute", minutes, minute, culture,
            value => value.ToString("00", culture),
            (value, focus) => Commit(hour24, value, second, "minute", value.ToString(CultureInfo.InvariantCulture), focus)));

        if (ShowSeconds)
        {
            var seconds = WithCurrent(TimeMath.SecondValues(SecondIncrement), second);
            _tabStops["second"] = second.ToString(CultureInfo.InvariantCulture);
            root.Children.Add(BuildColumn("second", "Second", seconds, second, culture,
                value => value.ToString("00", culture),
                (value, focus) => Commit(hour24, minute, value, "second", value.ToString(CultureInfo.InvariantCulture), focus)));
        }

        if (twelveHour)
        {
            var periods = new List<int> { 0, 1 };
            _tabStops["period"] = isPm ? "pm" : "am";
            root.Children.Add(BuildColumn("period", "AM or PM", periods, isPm ? 1 : 0, culture,
                value => value == 0 ? culture.DateTimeFormat.AMDesignator : culture.DateTimeFormat.PMDesignator,
                (value, focus) => Commit(
                    TimeMath.ToTwentyFourHour(displayHour, value == 1), minute, second, "period",
                    value == 1 ? "pm" : "am", focus)));
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
            string optionText = text(value);
            var button = new UIButton
            {
                Role = Role.Option,
                Disabled = !IsEnabled,
                TabIndex = isSelected ? 0 : -1,
                AriaLabel = optionText,
                Children = new NodeList { new UIText(optionText) },
            };
            button.Attributes["type"] = "button";
            button.Attributes["part"] = "option";
            button.Attributes["data-column"] = column;
            button.Attributes["data-value"] = column == "period"
                ? (value == 0 ? "am" : "pm")
                : value.ToString(CultureInfo.InvariantCulture);
            button.Attributes["aria-selected"] = PartProps.Bool(isSelected);
            button.Attributes["data-selected"] = isSelected ? "" : null;

            var reference = new Ref<Element>();
            _refs[column + ":" + button.Attributes["data-value"]] = reference;
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
        var order = new List<string> { "hour", "minute" };
        if (ShowSeconds) order.Add("second");
        if (IsTwelveHourClock(ClockIdentifier)) order.Add("period");
        int index = order.IndexOf(column);
        if (index < 0) return;
        index += direction;
        if (index < 0 || index >= order.Count) return;
        string target = order[index];
        if (!_tabStops.TryGetValue(target, out string? value)) return;
        _focusKey = target + ":" + value;
        _focusRequested = true;
        Invalidate();
    }

    void Commit(int hour, int minute, int second, string focusColumn, string focusValue, bool focus)
    {
        var time = TimeMath.Compose(hour, minute, second);
        if (focus)
        {
            _focusKey = focusColumn + ":" + focusValue;
            _focusRequested = true;
        }
        _selected.Set(time);
        Invalidate();
    }

    static bool IsTwelveHourClock(string? identifier) =>
        string.Equals(identifier, ClockIdentifiers.TwelveHourClock, StringComparison.OrdinalIgnoreCase);

    static IReadOnlyList<int> WithCurrent(IReadOnlyList<int> values, int current)
    {
        for (int i = 0; i < values.Count; i++)
            if (values[i] == current) return values;
        var list = new List<int>(values) { current };
        list.Sort();
        return list;
    }
}

static class TimePickerStyles
{
    internal const string StyleSheet = """
        @layer aw-time-picker-defaults {
          :where(.aw-time-picker) {
            display: inline-flex;
            flex-direction: row;
            align-items: stretch;
            gap: 2px;
            min-width: 0;
          }
          :where(.aw-time-picker > [part="hour"],
                 .aw-time-picker > [part="minute"],
                 .aw-time-picker > [part="second"],
                 .aw-time-picker > [part="period"]) {
            display: flex;
            flex-direction: column;
            overflow: auto;
            max-height: 220px;
            min-width: 52px;
          }
          :where(.aw-time-picker [part="option"]) {
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
          :where(.aw-time-picker [part="option"][data-selected]) {
            background: color-mix(in srgb, currentColor 16%, transparent);
          }
        }
        """;
}
