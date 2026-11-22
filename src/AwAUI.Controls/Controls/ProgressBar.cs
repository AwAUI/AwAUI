using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>Pure range and text helpers for the progress controls.</summary>
public static class ProgressMath
{
    /// <summary>Normalizes a range: swaps inverted bounds and replaces non-finite values with 0 and 100.</summary>
    public static void NormalizeRange(double minimum, double maximum, out double min, out double max)
    {
        min = IsFinite(minimum) ? minimum : 0;
        max = IsFinite(maximum) ? maximum : 100;
        if (max < min)
        {
            double swap = min;
            min = max;
            max = swap;
        }
    }

    /// <summary>Clamps a value into the range. NaN becomes the lower bound.</summary>
    public static double ClampValue(double value, double min, double max)
    {
        if (double.IsNaN(value)) return min;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>Returns the completed fraction in [0, 1]. A zero-width range reports 0.</summary>
    public static double Fraction(double value, double min, double max)
    {
        if (!(max > min)) return 0;
        double clamped = ClampValue(value, min, max);
        return (clamped - min) / (max - min);
    }

    /// <summary>Returns the completed percentage in [0, 100].</summary>
    public static double Percentage(double value, double min, double max) => Fraction(value, min, max) * 100;

    /// <summary>
    /// Formats the progress text. The format argument is the completed percentage (0–100). Null or empty uses
    /// <c>{0:0}%</c>; an invalid format falls back to the rounded percentage.
    /// </summary>
    public static string FormatText(string? format, double value, double min, double max)
    {
        double percent = Percentage(value, min, max);
        string pattern = string.IsNullOrEmpty(format) ? "{0:0}%" : format!;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, pattern, percent);
        }
        catch (FormatException)
        {
            return percent.ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }

    static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>
/// A horizontal progress indicator. The value is clamped into <see cref="Minimum"/> / <see cref="Maximum"/> and exposed
/// as <c>aria-valuenow</c>; <see cref="IsIndeterminate"/> omits the current value and sets <c>aria-busy</c>. The optional
/// text is the completed percentage formatted by <see cref="ProgressTextFormat"/>.
/// </summary>
public sealed partial class ProgressBar : Control
{
    readonly ControllableState<double> _value = new();

    protected override string GlobalStyleSheet => ProgressBarStyles.StyleSheet;

    /// <summary>Controlled value; null uses <see cref="DefaultValue"/>.</summary>
    public double? Value { get; set; }

    /// <summary>Initial uncontrolled value. Defaults to 0.</summary>
    public double DefaultValue { get; set; }

    /// <summary>Reports a value change. Supports <c>@bind-Value</c>.</summary>
    public Action<double>? ValueChanged { get; set; }

    public double Minimum { get; set; }

    public double Maximum { get; set; } = 100;

    /// <summary>Shows an animated, value-less indicator and omits <c>aria-valuenow</c>.</summary>
    public bool IsIndeterminate { get; set; }

    /// <summary>Shows the formatted percentage next to the track.</summary>
    public bool ShowProgressText { get; set; }

    /// <summary>Format applied to the completed percentage. Defaults to <c>{0:0}%</c>.</summary>
    public string? ProgressTextFormat { get; set; } = "{0:0}%";

    protected override Node Build()
    {
        double raw = _value.Sync(Value.HasValue, Value.GetValueOrDefault(), DefaultValue, ValueChanged);
        ProgressMath.NormalizeRange(Minimum, Maximum, out double min, out double max);
        double clamped = ProgressMath.ClampValue(raw, min, max);
        double fraction = ProgressMath.Fraction(clamped, min, max);
        string text = ProgressMath.FormatText(ProgressTextFormat, clamped, min, max);

        var root = Apply(new Div { Role = Role.ProgressBar }, "aw-progressbar");
        root.AriaLabel = AriaLabel;
        root.Attributes["aria-valuemin"] = Number(min);
        root.Attributes["aria-valuemax"] = Number(max);
        root.Attributes["aria-valuenow"] = IsIndeterminate ? null : Number(clamped);
        root.Attributes["aria-valuetext"] = IsIndeterminate || !ShowProgressText ? null : text;
        root.Attributes["aria-busy"] = IsIndeterminate ? "true" : null;
        root.Attributes["data-state"] = IsIndeterminate ? "indeterminate" : "determinate";
        root.Attributes["data-indeterminate"] = IsIndeterminate ? "" : null;

        var track = new Div();
        track.Attributes["part"] = "track";
        var indicator = new Div();
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["data-state"] = IsIndeterminate ? "indeterminate" : "determinate";
        indicator.Style = IsIndeterminate ? "" : "width:" + Percent(fraction) + ";";
        track.Children.Add(indicator);
        root.Children.Add(track);

        if (ShowProgressText && !IsIndeterminate)
        {
            var label = new Div { Children = new NodeList { new UIText(text) } };
            label.Attributes["part"] = "progress-text";
            root.Children.Add(label);
        }

        return root;
    }

    static string Number(double value)
    {
        if (value == Math.Truncate(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    static string Percent(double fraction) =>
        (fraction * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
}

static class ProgressBarStyles
{
    internal const string StyleSheet = """
        @layer aw-progressbar-defaults {
          :where(.aw-progressbar) {
            display: inline-flex;
            flex-direction: row;
            align-items: center;
            gap: 8px;
            min-width: 80px;
          }
          :where(.aw-progressbar [part="track"]) {
            position: relative;
            flex: 1 1 auto;
            min-width: 0;
            height: 4px;
            overflow: hidden;
            background: color-mix(in srgb, currentColor 18%, transparent);
            border-radius: 2px;
          }
          :where(.aw-progressbar [part="indicator"]) {
            height: 100%;
            background: currentColor;
            border-radius: inherit;
          }
          :where(.aw-progressbar [part="indicator"][data-state="indeterminate"]) {
            width: 30%;
            animation: aw-progressbar-slide 1.2s ease-in-out infinite;
          }
          :where(.aw-progressbar [part="progress-text"]) {
            flex: 0 0 auto;
            white-space: nowrap;
            font-variant-numeric: tabular-nums;
          }
        }
        @keyframes aw-progressbar-slide {
          from { margin-left: -30%; }
          to { margin-left: 100%; }
        }
        """;
}
