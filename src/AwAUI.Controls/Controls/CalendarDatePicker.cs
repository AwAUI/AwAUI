using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>Date display format used by <see cref="CalendarDatePicker"/>.</summary>
public enum CalendarDatePickerFormat : byte
{
    /// <summary>The culture's short date pattern.</summary>
    Short,

    /// <summary>The culture's long date pattern.</summary>
    Long,

    /// <summary>The pattern in <see cref="CalendarDatePicker.CustomDateFormatString"/>.</summary>
    Custom,
}

/// <summary>
/// A text field with a dropdown <see cref="Calendar"/>. Typing a date and pressing Enter commits it; invalid text is
/// marked with <c>aria-invalid</c> while editing and reverts to the last valid date when committed. Picking a day in the
/// calendar updates the text and closes the dropdown.
/// </summary>
public sealed partial class CalendarDatePicker : Control
{
    readonly ControllableState<DateTime?> _selectedDate = new();
    readonly ControllableState<bool> _open = new();
    readonly Ref<Element> _anchor = new();
    CultureInfo _culture = CultureInfo.CurrentCulture;
    bool _editing;
    string? _editText;
    bool _invalid;

    protected override string GlobalStyleSheet => CalendarDatePickerStyles.StyleSheet;

    /// <summary>Controlled selected date; null means no date unless <see cref="IsSelectedDateControlled"/> is set.</summary>
    public DateTime? SelectedDate { get; set; }

    /// <summary>Initial uncontrolled date.</summary>
    public DateTime? DefaultSelectedDate { get; set; }

    /// <summary>Treats a null <see cref="SelectedDate"/> as a controlled empty selection.</summary>
    public bool IsSelectedDateControlled { get; set; }

    /// <summary>Reports a selection change. Supports <c>@bind-SelectedDate</c>.</summary>
    public Action<DateTime?>? SelectedDateChanged { get; set; }

    /// <summary>The text shown in the field. Null lets the control format the selected date.</summary>
    public string? Text { get; set; }

    /// <summary>Initial text when <see cref="Text"/> is null and no date is selected.</summary>
    public string? DefaultText { get; set; }

    /// <summary>Reports text changes while the user types.</summary>
    public Action<string>? TextChanged { get; set; }

    /// <summary>Controls the dropdown; null uses <see cref="DefaultIsDropDownOpen"/>.</summary>
    public bool? IsDropDownOpen { get; set; }

    public bool DefaultIsDropDownOpen { get; set; }

    /// <summary>Reports a dropdown change and accepts a dismissal.</summary>
    public Action<bool>? DropDownOpenChanged { get; set; }

    /// <summary>How <see cref="SelectedDate"/> is rendered and, for <see cref="CalendarDatePickerFormat.Custom"/>, parsed.</summary>
    public CalendarDatePickerFormat DateFormat { get; set; } = CalendarDatePickerFormat.Short;

    /// <summary>Custom date format string. When set it takes precedence over <see cref="DateFormat"/>.</summary>
    public string? Format { get; set; }

    /// <summary>Custom format string used when <see cref="DateFormat"/> is <see cref="CalendarDatePickerFormat.Custom"/>.</summary>
    public string? CustomDateFormatString { get; set; }

    /// <summary>
    /// Culture used to format and parse the text. Defaults to <see cref="CultureInfo.CurrentCulture"/>; assigning null
    /// restores the current culture.
    /// </summary>
    public CultureInfo Culture
    {
        get => _culture;
        set => _culture = value ?? CultureInfo.CurrentCulture;
    }

    public string? Watermark { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsEnabled { get; set; } = true;

    public DateTime? DisplayDateStart { get; set; }
    public DateTime? DisplayDateEnd { get; set; }
    public int MinYear { get; set; } = 1900;
    public int MaxYear { get; set; } = 2100;
    public DayOfWeek? FirstDayOfWeek { get; set; }
    public bool IsTodayHighlighted { get; set; } = true;

    protected override Node Build()
    {
        var culture = Culture;
        var selected = _selectedDate.Sync(IsSelectedDateControlled || SelectedDate.HasValue,
            SelectedDate.GetValueOrDefault(), DefaultSelectedDate.GetValueOrDefault(), SelectedDateChanged);
        bool open = _open.Sync(IsDropDownOpen, DefaultIsDropDownOpen, DropDownOpenChanged) && IsEnabled;

        string text = Text ?? (_editing ? _editText ?? "" : selected is { } date
            ? FormatDate(date, culture)
            : DefaultText ?? "");

        var root = Apply(new Div { Role = Role.Group }, "aw-calendar-date-picker");
        root.AriaLabel = AriaLabel ?? "Date";

        var input = new Input
        {
            Role = Role.TextBox,
            Value = text,
            Placeholder = Watermark,
            ReadOnly = IsReadOnly,
            Disabled = !IsEnabled,
            OnInput = e => OnInput(e),
            OnSubmit = _ => Commit(),
            OnChange = _ => Commit(),
            OnKeyDown = e => OnKeyDown(e, open),
        };
        input.Attributes["part"] = "input";
        input.Attributes["aria-invalid"] = _invalid ? "true" : null;

        var button = new UIButton
        {
            Disabled = !IsEnabled,
            AriaLabel = "Open calendar",
            Children = new NodeList { new UIText("▾") },
        };
        button.Attributes["type"] = "button";
        button.Attributes["part"] = "button";
        button.Attributes["aria-haspopup"] = "dialog";
        button.Attributes["aria-expanded"] = PartProps.Bool(open);
        button.OnClick = e =>
        {
            if (!e.DefaultPrevented && IsEnabled) _open.Set(!open);
        };

        var field = new Div { Children = new NodeList { input, button }, Ref = _anchor };
        field.Attributes["part"] = "field";

        var calendar = new Calendar
        {
            SelectedDate = selected,
            DefaultSelectedDate = selected ?? DefaultSelectedDate,
            IsSelectedDateControlled = IsSelectedDateControlled,
            SelectedDateChanged = OnCalendarSelect,
            DisplayDateStart = DisplayDateStart,
            DisplayDateEnd = DisplayDateEnd,
            MinYear = MinYear,
            MaxYear = MaxYear,
            FirstDayOfWeek = FirstDayOfWeek,
            IsTodayHighlighted = IsTodayHighlighted,
            Culture = culture,
            IsEnabled = IsEnabled,
        };

        var popup = new Popup
        {
            Anchor = _anchor,
            IsOpen = open,
            IsOpenChanged = value => _open.Set(value),
            Side = Side.Bottom,
            Align = PopperAlign.Start,
            SideOffset = 4,
            AutoFocus = true,
            Content = calendar,
        };

        root.Children.Add(field);
        root.Children.Add(popup);
        return root;
    }

    void OnInput(InputEvent e)
    {
        if (IsReadOnly || !IsEnabled) return;
        _editing = true;
        _editText = e.Value;
        _invalid = !TryParseDate(e.Value, out _);
        TextChanged?.Invoke(e.Value);
        Invalidate();
    }

    void Commit()
    {
        if (!_editing || IsReadOnly) return;
        string candidate = _editText ?? "";
        if (TryParseDate(candidate, out var parsed))
        {
            _editing = false;
            _editText = null;
            _invalid = false;
            if (_selectedDate.Value is not { } current || current.Date != parsed)
                _selectedDate.Set(parsed);
        }
        else
        {
            // 无效输入不改变当前选择：回退到上一个有效文本。
            _editing = false;
            _editText = null;
            _invalid = false;
        }
        Invalidate();
    }

    void OnKeyDown(KeyEvent e, bool open)
    {
        if (e.DefaultPrevented) return;
        if (e.Key == "Escape")
        {
            if (open)
            {
                e.PreventDefault();
                _open.Set(false);
            }
            else if (_editing)
            {
                e.PreventDefault();
                _editing = false;
                _editText = null;
                _invalid = false;
                Invalidate();
            }
            return;
        }
        if (e.Key == "ArrowDown" && (e.Modifiers & KeyModifiers.Alt) != 0 && IsEnabled)
        {
            e.PreventDefault();
            _open.Set(true);
        }
    }

    void OnCalendarSelect(DateTime? date)
    {
        _editing = false;
        _editText = null;
        _invalid = false;
        _selectedDate.Set(date);
        _open.Set(false);
    }

    string FormatDate(DateTime value, CultureInfo culture) =>
        DateText.Format(value, culture, FormatPattern(culture));

    string? FormatPattern(CultureInfo culture)
    {
        if (!string.IsNullOrEmpty(Format)) return Format;
        return DateFormat switch
        {
            CalendarDatePickerFormat.Long => culture.DateTimeFormat.LongDatePattern,
            CalendarDatePickerFormat.Custom => CustomDateFormatString,
            _ => null,
        };
    }

    bool TryParseDate(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var culture = Culture;
        string? pattern = FormatPattern(culture);
        if (!string.IsNullOrEmpty(pattern) && DateText.TryParseExact(text, culture, pattern, out value)) return true;
        return DateText.TryParse(text, culture, out value);
    }
}

static class CalendarDatePickerStyles
{
    internal const string StyleSheet = """
        @layer aw-calendar-date-picker-defaults {
          :where(.aw-calendar-date-picker) {
            display: inline-flex;
            flex-direction: column;
            align-items: stretch;
            min-width: 0;
          }
          :where(.aw-calendar-date-picker [part="field"]) {
            display: flex;
            flex-direction: row;
            align-items: center;
            min-width: 0;
          }
          :where(.aw-calendar-date-picker [part="input"]) {
            flex: 1 1 auto;
            min-width: 0;
          }
          :where(.aw-calendar-date-picker [part="button"]) {
            flex: 0 0 auto;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            min-width: 24px;
            min-height: 24px;
          }
        }
        """;
}
