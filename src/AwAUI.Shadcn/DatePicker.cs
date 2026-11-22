using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn;

/// <summary>
/// A button that shows the selected date (or range) and opens a <see cref="Calendar"/> in a popover.
/// Selection is controlled through <see cref="SelectedDate"/> / <see cref="SelectedDateChanged"/> (single mode) or
/// <see cref="RangeStart"/> / <see cref="RangeEnd"/> / <see cref="RangeChanged"/> (range mode), and uncontrolled through the
/// corresponding <c>Default…</c> values. The popover closes after a complete selection unless <see cref="CloseOnSelect"/> is
/// false; in range mode it stays open after the first click and closes when the second endpoint is chosen.
/// The trigger is keyboard operable (Enter/Space toggle, ArrowDown/ArrowUp open) and exposes
/// <c>aria-haspopup="dialog"</c> and <c>aria-expanded</c>.
/// </summary>
public sealed partial class DatePicker : Component
{
    const string TriggerClass =
        "inline-flex h-9 w-full min-w-0 items-center justify-between gap-2 rounded-md border border-input bg-transparent px-3 py-2 " +
        "text-left text-sm shadow-xs outline-none transition-[color,box-shadow] focus-visible:border-ring focus-visible:ring-[3px] " +
        "focus-visible:ring-ring/50 disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 dark:bg-input/30 dark:hover:bg-input/50";
    const string PlaceholderClass = "text-muted-foreground";
    const string ContentClass = "w-auto p-0";
    const string FooterClass = "flex items-center justify-end border-t p-2";
    const string ClearClass =
        "inline-flex h-8 items-center justify-center rounded-md px-2 text-sm font-medium text-muted-foreground " +
        "hover:bg-accent hover:text-accent-foreground focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 " +
        "disabled:pointer-events-none disabled:opacity-50";

    readonly ControllableState<DateTime?> _selected = new();
    readonly ControllableState<CalendarRange?> _range = new();
    readonly ControllableState<bool> _open = new();

    /// <summary>Selection model. <see cref="CalendarSelectionMode.Single"/> (default) or <see cref="CalendarSelectionMode.Range"/>.</summary>
    public CalendarSelectionMode Mode { get; set; } = CalendarSelectionMode.Single;

    // ---------------------------------------------------------------- single mode

    /// <summary>Controlled selection. A value only takes effect when non-null or <see cref="IsSelectedDateControlled"/> is set.</summary>
    public DateTime? SelectedDate { get; set; }

    /// <summary>Selection used before the user picks a date (uncontrolled).</summary>
    public DateTime? DefaultSelectedDate { get; set; }

    /// <summary>Treats <see cref="SelectedDate"/> as controlled even when it is <c>null</c> (no selection).</summary>
    public bool IsSelectedDateControlled { get; set; }

    /// <summary>Receives each selection and supports <c>@bind-SelectedDate</c>.</summary>
    public Action<DateTime?>? SelectedDateChanged { get; set; }

    /// <summary>Alias for <see cref="SelectedDateChanged"/>.</summary>
    public Action<DateTime?>? OnSelect { get; set; }

    // ---------------------------------------------------------------- range mode

    /// <summary>Controlled range start (null while no range is selected).</summary>
    public DateTime? RangeStart { get; set; }

    /// <summary>Controlled range end; null while only the first endpoint has been chosen.</summary>
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

    /// <summary>Text shown while no range is selected. Falls back to <see cref="Placeholder"/>.</summary>
    public string? RangePlaceholder { get; set; }

    /// <summary>Format string for each endpoint of a range. Defaults to <c>MMM d, yyyy</c>.</summary>
    public string? RangeFormat { get; set; } = "MMM d, yyyy";

    /// <summary>Text between the two formatted endpoints. Defaults to <c>" - "</c>.</summary>
    public string RangeSeparator { get; set; } = " - ";

    // ---------------------------------------------------------------- popover

    /// <summary>Controlled popover state; null leaves it uncontrolled.</summary>
    public bool? Open { get; set; }

    public bool DefaultOpen { get; set; }

    public Action<bool>? OpenChanged { get; set; }

    public Action<bool>? OnOpenChange { get; set; }

    /// <summary>Text shown while no date is selected.</summary>
    public string? Placeholder { get; set; } = "Pick a date";

    /// <summary>Standard date format string used for the single-mode trigger label. Defaults to the short date pattern.</summary>
    public string? Format { get; set; } = "d";

    /// <summary>Culture used for the label and the calendar. Defaults to <see cref="CultureInfo.CurrentCulture"/>.</summary>
    public CultureInfo? Culture { get; set; }

    public DateTime? MinDate { get; set; }

    public DateTime? MaxDate { get; set; }

    /// <summary>Alias for <see cref="MinDate"/>.</summary>
    public DateTime? FromDate { get; set; }

    /// <summary>Alias for <see cref="MaxDate"/>.</summary>
    public DateTime? ToDate { get; set; }

    public Func<DateTime, bool>? DisabledDates { get; set; }

    /// <summary>Alias for <see cref="DisabledDates"/>.</summary>
    public Func<DateTime, bool>? IsDateDisabled { get; set; }

    public DayOfWeek? FirstDayOfWeek { get; set; }

    /// <summary>First day of the week as 0 = Sunday … 6 = Saturday.</summary>
    public int? WeekStartsOn { get; set; }

    /// <summary>Number of consecutive months shown in the popover. Default 1.</summary>
    public int NumberOfMonths { get; set; } = 1;

    /// <summary>Controlled visible month of the popover calendar.</summary>
    public DateTime? Month { get; set; }

    /// <summary>Receives month navigation from the popover calendar.</summary>
    public Action<DateTime>? MonthChanged { get; set; }

    /// <summary>Month shown when the popover first opens without a selection. Defaults to the selection, then today.</summary>
    public DateTime? DefaultDisplayDate { get; set; }

    public bool ShowOutsideDays { get; set; } = true;

    /// <summary>Adds a Clear action to the popover footer.</summary>
    public bool ShowClear { get; set; }

    /// <summary>Text of the Clear action.</summary>
    public string ClearLabel { get; set; } = "Clear";

    /// <summary>Accessible name of the popover dialog.</summary>
    public string ContentLabel { get; set; } = "Choose date";

    /// <summary>Closes the popover after a date is selected. Default true.</summary>
    public bool CloseOnSelect { get; set; } = true;

    public Side Side { get; set; } = Side.Bottom;

    public PopperAlign Align { get; set; } = PopperAlign.Start;

    public float SideOffset { get; set; } = 4;

    public float CollisionPadding { get; set; } = 8;

    public Direction? Dir { get; set; }

    public bool Disabled { get; set; }

    /// <summary>Extra classes for the trigger button.</summary>
    public string? Class { get; set; }

    public Ref<Element>? Ref { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var culture = Culture ?? CultureInfo.CurrentCulture;
        bool rangeMode = Mode == CalendarSelectionMode.Range;

        DateTime? selected = _selected.Sync(
            IsSelectedDateControlled || SelectedDate is not null,
            SelectedDate,
            DefaultSelectedDate,
            SelectedDateChanged + OnSelect);
        selected = selected?.Date;

        CalendarRange? controlledRange = ControlledRange();
        CalendarRange? defaultRange = DefaultRange();
        CalendarRange? range = _range.Sync(
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

        bool open = _open.Sync(Open, DefaultOpen, OpenChanged + OnOpenChange);
        bool empty = rangeMode ? range is null : selected is null;

        var trigger = TabsParts.Style(new UIButton { Disabled = Disabled, Ref = Ref },
            "date-picker-trigger",
            TriggerClass + (empty ? " " + PlaceholderClass : ""),
            Class, Rest);
        trigger.Attributes["type"] = "button";
        trigger.Attributes["data-state"] = open ? "open" : "closed";
        trigger.Attributes["data-placeholder"] = empty ? "" : null;
        trigger.Attributes["data-empty"] = empty ? "true" : null;
        trigger.Attributes["data-disabled"] = Disabled ? "" : null;
        trigger.Attributes["data-selection-mode"] = rangeMode ? "range" : "single";
        trigger.Attributes["aria-disabled"] = TabsParts.Bool(Disabled);
        trigger.Children.Add(new UIText(Label(selected, range, culture)));
        trigger.OnKeyDown = EventComposition.Compose(trigger.OnKeyDown, e =>
        {
            if (e.DefaultPrevented || Disabled || open) return;
            if (e.Key is "ArrowDown" or "ArrowUp")
            {
                e.PreventDefault();
                _open.Set(true);
            }
        });

        var calendar = new Calendar
        {
            SelectionMode = rangeMode ? CalendarSelectionMode.Range : CalendarSelectionMode.Single,
            SelectedDate = rangeMode ? null : selected,
            IsSelectedDateControlled = !rangeMode,
            SelectedDateChanged = rangeMode ? null : OnDateSelected,
            RangeStart = rangeMode ? range?.Start : null,
            RangeEnd = rangeMode ? range?.End : null,
            IsRangeControlled = rangeMode,
            RangeChanged = rangeMode ? OnCalendarRange : null,
            DisplayDate = Month,
            DisplayDateChanged = MonthChanged,
            DefaultDisplayDate = DefaultDisplayDate ?? selected ?? range?.Start,
            NumberOfMonths = NumberOfMonths,
            MinDate = MinDate ?? FromDate,
            MaxDate = MaxDate ?? ToDate,
            DisabledDates = DisabledDates,
            IsDateDisabled = IsDateDisabled,
            FirstDayOfWeek = FirstDayOfWeek,
            WeekStartsOn = WeekStartsOn,
            ShowOutsideDays = ShowOutsideDays,
            Dir = Dir,
            Disabled = Disabled,
            Culture = culture,
            Class = "bg-transparent",
        };

        var contentChildren = new NodeList { calendar };
        if (ShowClear)
        {
            var footer = new Div { Children = new NodeList { BuildClearButton() } };
            footer.Attributes["data-slot"] = "date-picker-footer";
            footer.Class = FooterClass;
            contentChildren.Add(footer);
        }

        var content = new PopoverContent
        {
            Side = Side,
            Align = Align,
            SideOffset = SideOffset,
            CollisionPadding = CollisionPadding,
            Class = ContentClass,
            Children = contentChildren,
            Rest = new AttributeDictionary { ["aria-label"] = ContentLabel },
        };

        return new Popover
        {
            Open = open,
            OnOpenChange = SetOpen,
            Children = new NodeList
            {
                new PopoverTrigger { AsChild = true, Children = new NodeList { trigger } },
                content,
            },
        };
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

    string Label(DateTime? selected, CalendarRange? range, CultureInfo culture)
    {
        if (Mode == CalendarSelectionMode.Range)
        {
            if (range is not { } value) return RangePlaceholder ?? Placeholder ?? "";
            var format = string.IsNullOrEmpty(RangeFormat) ? (string.IsNullOrEmpty(Format) ? "d" : Format) : RangeFormat;
            var start = value.Start.ToString(format, culture);
            if (value.End is not { } end) return start;
            return start + RangeSeparator + end.ToString(format, culture);
        }

        if (selected is not { } date) return Placeholder ?? "";
        var singleFormat = string.IsNullOrEmpty(Format) ? "d" : Format;
        return date.ToString(singleFormat, culture);
    }

    UIButton BuildClearButton()
    {
        var button = TabsParts.Style(new UIButton { Disabled = Disabled }, "date-picker-clear", ClearClass, null, null);
        button.Attributes["type"] = "button";
        button.Attributes["data-disabled"] = Disabled ? "" : null;
        button.Attributes["aria-disabled"] = TabsParts.Bool(Disabled);
        button.Children.Add(new UIText(ClearLabel));
        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (e.DefaultPrevented || Disabled) return;
            _selected.Set(null);
            _range.Set(null);
            _open.Set(false);
        });
        return button;
    }

    void OnDateSelected(DateTime? value)
    {
        _selected.Set(value?.Date);
        if (value is not null && CloseOnSelect) _open.Set(false);
    }

    void OnCalendarRange(DateTime? start, DateTime? end)
    {
        CalendarRange? range = start is null ? null : new CalendarRange(start.Value.Date, end?.Date);
        _range.Set(range);
        if (range is { IsComplete: true } && CloseOnSelect) _open.Set(false);
    }

    void SetOpen(bool open)
    {
        if (open && Disabled) return;
        _open.Set(open);
    }
}
