using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>
/// A numeric text box with increment and decrement buttons.
/// </summary>
/// <remarks>
/// Type a number or use the spinner buttons. Up and Down change the value by <see cref="Increment"/>, Page Up and Page
/// Down by ten increments, and Home and End jump to <see cref="Minimum"/> and <see cref="Maximum"/>. Enter and leaving the
/// field commit the typed text: invalid text reverts, and with <see cref="ClipValueToMinMax"/> the value is clamped into the
/// range. The spinner buttons are not tab stops; the input is the single tab stop and exposes the value through
/// <c>aria-valuenow</c>. The field formats and parses with <see cref="Culture"/> (the current culture by default), so
/// decimal separators and group separators follow the user's locale; the ARIA value attributes stay invariant.
/// </remarks>
public sealed partial class NumericUpDown : Control
{
    readonly ControllableState<double?> _value = new();
    CultureInfo _culture = CultureInfo.CurrentCulture;
    bool _editing;
    string? _editText;
    bool _invalid;

    protected override string GlobalStyleSheet => NumericUpDownStyles.StyleSheet;

    /// <summary>Current value. Null shows no value. Controlled when set; otherwise <see cref="DefaultValue"/> applies.</summary>
    public double? Value { get; set; }

    public double? DefaultValue { get; set; }

    /// <summary>Requests a change to <see cref="Value"/>. <c>@bind</c>-able.</summary>
    public Action<double?>? ValueChanged { get; set; }

    public double Minimum { get; set; } = double.NegativeInfinity;

    public double Maximum { get; set; } = double.PositiveInfinity;

    public double Increment { get; set; } = 1;

    /// <summary>Standard or custom numeric format string used when the field is not being edited.</summary>
    public string? FormatString { get; set; }

    /// <summary>
    /// Culture used to format the displayed value and parse typed text. Defaults to <see cref="CultureInfo.CurrentCulture"/>;
    /// assigning null restores the current culture. The ARIA value attributes always use the invariant culture so assistive
    /// technology can parse them.
    /// </summary>
    public CultureInfo Culture
    {
        get => _culture;
        set => _culture = value ?? CultureInfo.CurrentCulture;
    }

    public bool ShowButtonSpinner { get; set; } = true;

    /// <summary>Clamp typed values into <see cref="Minimum"/> and <see cref="Maximum"/> when the field is committed.</summary>
    public bool ClipValueToMinMax { get; set; }

    public bool IsReadOnly { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? Watermark { get; set; }

    public string? IncreaseButtonAriaLabel { get; set; } = "Increase";

    public string? DecreaseButtonAriaLabel { get; set; } = "Decrease";

    protected override Node Build()
    {
        double? value = _value.Sync(Value.HasValue, Value.GetValueOrDefault(), DefaultValue ?? 0, ValueChanged);
        double? clamped = value is { } current ? Clamp(current) : null;

        var input = new Input
        {
            Role = Role.TextBox,
            Value = _editing ? _editText : Format(clamped),
            Placeholder = Watermark,
            ReadOnly = IsReadOnly,
            Disabled = !IsEnabled,
            InputType = "text",
            OnInput = e => OnInput(e),
            OnChange = _ => Commit(),
            OnKeyDown = e => OnKeyDown(e),
        };
        input.Attributes["inputmode"] = "decimal";
        input.Attributes["part"] = "input";
        input.Attributes["role"] = "spinbutton";
        input.Attributes["aria-valuemin"] = double.IsNegativeInfinity(Minimum) ? null : FormatInvariant(Minimum);
        input.Attributes["aria-valuemax"] = double.IsPositiveInfinity(Maximum) ? null : FormatInvariant(Maximum);
        input.Attributes["aria-valuenow"] = clamped is { } now ? FormatInvariant(now) : null;
        input.Attributes["aria-invalid"] = _invalid ? "true" : null;

        var box = new Div { Children = [input] };
        box.Attributes["part"] = "container";

        var root = new Div { Children = [box] };
        root.Attributes["role"] = "group";
        root.Attributes["data-disabled"] = IsEnabled ? null : "";

        if (ShowButtonSpinner)
        {
            var spinner = new Div();
            spinner.Attributes["part"] = "spinner";
            spinner.Attributes["aria-hidden"] = "true";
            spinner.Children.Add(SpinButton("up", IncreaseButtonAriaLabel, +1));
            spinner.Children.Add(SpinButton("down", DecreaseButtonAriaLabel, -1));
            root.Children.Add(spinner);
        }

        return Apply(root, "aw-numeric-updown");
    }

    UIButton SpinButton(string part, string? label, int direction)
    {
        var indicator = new Span();
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["aria-hidden"] = "true";
        var button = new UIButton
        {
            Disabled = !IsEnabled || IsReadOnly,
            Children = [indicator],
            OnClick = e =>
            {
                if (e.DefaultPrevented || IsReadOnly) return;
                e.PreventDefault();
                Step(direction);
            },
        };
        button.Class = "aw-numeric-updown-button";
        button.AriaLabel = label;
        button.Attributes["part"] = part;
        button.Attributes["tabindex"] = "-1";
        return button;
    }

    void OnInput(InputEvent e)
    {
        if (IsReadOnly) return;
        _editing = true;
        _editText = e.Value;
        _invalid = !TryParse(e.Value, out _);
        if (TryParse(e.Value, out double parsed) && (_value.Value != parsed))
        {
            var next = ClipValueToMinMax ? Clamp(parsed) : parsed;
            if (_value.Value != next) _value.Set(next);
        }
        else
        {
            Invalidate();
        }
    }

    void Commit()
    {
        if (!_editing) return;
        _editing = false;
        _editText = null;
        if (!_invalid && TryParse(_editText ?? "", out double parsed) && ClipValueToMinMax)
        {
            var next = Clamp(parsed);
            if (_value.Value != next) _value.Set(next);
        }
        else if (_invalid)
        {
            _invalid = false;
        }
        Invalidate();
    }

    void OnKeyDown(KeyEvent e)
    {
        if (e.DefaultPrevented || e.Modifiers != KeyModifiers.None) return;
        if (e.Key == "Enter" || e.Key == "Tab")
        {
            Commit();
            return;
        }
        int direction = e.Key switch
        {
            "ArrowUp" => +1,
            "ArrowDown" => -1,
            "PageUp" => +10,
            "PageDown" => -10,
            _ => 0,
        };
        if (direction != 0)
        {
            e.PreventDefault();
            Step(direction);
            return;
        }
        if (e.Key == "Home" && IsFinite(Minimum)) { e.PreventDefault(); SetValue(Minimum); }
        else if (e.Key == "End" && IsFinite(Maximum)) { e.PreventDefault(); SetValue(Maximum); }
    }

    void Step(int increments)
    {
        _editing = false;
        _editText = null;
        _invalid = false;
        double current = _value.Value ?? Clamp(0);
        SetValue(current + Increment * increments);
    }

    void SetValue(double value)
    {
        double next = Clamp(value);
        _value.Set(next);
    }

    double Clamp(double value)
    {
        if (double.IsNaN(value)) return IsFinite(Minimum) ? Minimum : 0;
        if (Minimum > Maximum) return value;
        return Math.Max(Minimum, Math.Min(Maximum, value));
    }

    static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    bool TryParse(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, Culture, out value) && IsFinite(value);

    string? Format(double? value) =>
        value is not { } number ? null
            : string.IsNullOrEmpty(FormatString)
                ? number.ToString(Culture)
                : number.ToString(FormatString, Culture);

    static string FormatInvariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}

static class NumericUpDownStyles
{
    internal const string StyleSheet = """
        @layer aw-numeric-updown-defaults {
          :where(.aw-numeric-updown) {
            display: inline-flex;
            flex-direction: row;
            align-items: stretch;
            box-sizing: border-box;
            min-width: 0;
          }
          :where(.aw-numeric-updown > [part="container"]) {
            display: flex;
            flex-direction: row;
            align-items: center;
            flex: 1 1 auto;
            min-width: 0;
          }
          :where(.aw-numeric-updown [part="input"]) {
            flex: 1 1 auto;
            min-width: 0;
            text-align: end;
          }
          :where(.aw-numeric-updown > [part="spinner"]) {
            display: flex;
            flex-direction: column;
            flex: 0 0 auto;
            align-self: stretch;
          }
          :where(.aw-numeric-updown [part="up"]),
          :where(.aw-numeric-updown [part="down"]) {
            display: flex;
            flex: 1 1 50%;
            align-items: center;
            justify-content: center;
            min-height: 0;
            padding: 0 4px;
            border: 0;
          }
          :where(.aw-numeric-updown [part="indicator"]) {
            width: 0;
            height: 0;
            border-inline: 4px solid transparent;
          }
          :where(.aw-numeric-updown [part="up"] > [part="indicator"]) {
            border-bottom: 5px solid currentColor;
          }
          :where(.aw-numeric-updown [part="down"] > [part="indicator"]) {
            border-top: 5px solid currentColor;
          }
        }
        """;
}
