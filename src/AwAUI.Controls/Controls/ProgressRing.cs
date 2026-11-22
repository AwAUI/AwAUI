using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Svg;
using AwAUI.UI;
using UISvg = AwAUI.UI.Svg;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>
/// A circular progress indicator. It exposes the same value API as <see cref="ProgressBar"/> and draws a determinate ring
/// with inline SVG (the renderer paints SVG strokes and dashes, whereas background <c>conic-gradient</c> is parsed but not
/// painted by the box painter). <see cref="IsIndeterminate"/> renders a quarter arc that spins; the current value is then
/// omitted from ARIA.
/// </summary>
public sealed partial class ProgressRing : Control
{
    readonly ControllableState<double> _value = new();

    protected override string GlobalStyleSheet => ProgressRingStyles.StyleSheet;

    /// <summary>Controlled value; null uses <see cref="DefaultValue"/>.</summary>
    public double? Value { get; set; }

    /// <summary>Initial uncontrolled value. Defaults to 0.</summary>
    public double DefaultValue { get; set; }

    /// <summary>Reports a value change. Supports <c>@bind-Value</c>.</summary>
    public Action<double>? ValueChanged { get; set; }

    public double Minimum { get; set; }

    public double Maximum { get; set; } = 100;

    /// <summary>Shows the animated value-less arc and omits <c>aria-valuenow</c>.</summary>
    public bool IsIndeterminate { get; set; }

    /// <summary>Shows the formatted percentage after the ring.</summary>
    public bool ShowProgressText { get; set; }

    /// <summary>Format applied to the completed percentage. Defaults to <c>{0:0}%</c>.</summary>
    public string? ProgressTextFormat { get; set; } = "{0:0}%";

    /// <summary>Outer diameter in logical pixels. Defaults to 40.</summary>
    public double Diameter { get; set; } = 40;

    /// <summary>Stroke thickness in logical pixels. Defaults to 4.</summary>
    public double Thickness { get; set; } = 4;

    protected override Node Build()
    {
        double raw = _value.Sync(Value.HasValue, Value.GetValueOrDefault(), DefaultValue, ValueChanged);
        ProgressMath.NormalizeRange(Minimum, Maximum, out double min, out double max);
        double clamped = ProgressMath.ClampValue(raw, min, max);
        double fraction = ProgressMath.Fraction(clamped, min, max);
        string text = ProgressMath.FormatText(ProgressTextFormat, clamped, min, max);

        var root = Apply(new Div { Role = Role.ProgressBar }, "aw-progress-ring");
        root.AriaLabel = AriaLabel;
        root.Attributes["aria-valuemin"] = Number(min);
        root.Attributes["aria-valuemax"] = Number(max);
        root.Attributes["aria-valuenow"] = IsIndeterminate ? null : Number(clamped);
        root.Attributes["aria-valuetext"] = IsIndeterminate || !ShowProgressText ? null : text;
        root.Attributes["aria-busy"] = IsIndeterminate ? "true" : null;
        root.Attributes["data-state"] = IsIndeterminate ? "indeterminate" : "determinate";
        root.Attributes["data-indeterminate"] = IsIndeterminate ? "" : null;

        double diameter = Diameter > 0 && !double.IsNaN(Diameter) && !double.IsInfinity(Diameter) ? Diameter : 40;
        double thickness = Thickness > 0 && !double.IsNaN(Thickness) && !double.IsInfinity(Thickness) ? Thickness : 4;
        double viewStroke = thickness * 100 / diameter;
        double radius = 50 - viewStroke / 2;
        double circumference = 2 * Math.PI * radius;

        var markup = BuildMarkup(fraction, IsIndeterminate, viewStroke, radius, circumference);
        var svg = new UISvg(SvgDocument.Parse(markup))
        {
            Width = (float)diameter,
            Height = (float)diameter,
        };
        svg.Attributes["part"] = "svg";
        svg.Style = "display:block;";
        root.Children.Add(svg);

        if (ShowProgressText && !IsIndeterminate)
        {
            var label = new Div { Children = new NodeList { new UIText(text) } };
            label.Attributes["part"] = "progress-text";
            root.Children.Add(label);
        }

        return root;
    }

    static string BuildMarkup(double fraction, bool indeterminate, double stroke, double radius, double circumference)
    {
        string geometry = "cx=\"50\" cy=\"50\" r=\"" + Number(radius) + "\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"" + Number(stroke) + "\"";
        string markup = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\">"
            + "<circle " + geometry + " opacity=\"0.2\"/>";
        if (indeterminate)
        {
            double visible = circumference * 0.25;
            markup += "<circle " + geometry + " stroke-dasharray=\"" + Number(visible) + " " + Number(circumference - visible)
                + "\" stroke-linecap=\"round\" transform=\"rotate(-90 50 50)\"/>";
        }
        else
        {
            markup += "<circle " + geometry + " stroke-dasharray=\"" + Number(circumference)
                + "\" stroke-dashoffset=\"" + Number(circumference * (1 - fraction))
                + "\" stroke-linecap=\"round\" transform=\"rotate(-90 50 50)\"/>";
        }
        return markup + "</svg>";
    }

    static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

static class ProgressRingStyles
{
    internal const string StyleSheet = """
        @layer aw-progress-ring-defaults {
          :where(.aw-progress-ring) {
            display: inline-flex;
            flex-direction: row;
            align-items: center;
            gap: 8px;
          }
          :where(.aw-progress-ring [part="svg"]) {
            flex: 0 0 auto;
            color: currentColor;
          }
          :where(.aw-progress-ring[data-indeterminate] [part="svg"]) {
            animation: aw-progress-ring-spin 1s linear infinite;
          }
          :where(.aw-progress-ring [part="progress-text"]) {
            flex: 0 0 auto;
            white-space: nowrap;
            font-variant-numeric: tabular-nums;
          }
        }
        @keyframes aw-progress-ring-spin {
          to { transform: rotate(360deg); }
        }
        """;
}
