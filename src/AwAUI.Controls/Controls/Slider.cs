using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>A horizontal slider for selecting one numeric value.</summary>
public sealed partial class Slider : Control
{
    readonly SliderBehavior _slider = new();

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;

    /// <summary>The positive increment used by the arrow keys and to snap pointer input.</summary>
    public double SmallChange { get; set; } = 1;

    /// <summary>The controlled value. Leave null to use the slider's own state.</summary>
    public double? Value { get; set; }

    /// <summary>The initial uncontrolled value. Defaults to <see cref="Minimum"/>.</summary>
    public double? DefaultValue { get; set; }

    /// <summary>Reports value changes and supports <c>@bind-Value</c>.</summary>
    public Action<double>? ValueChanged { get; set; }

    /// <summary>An optional text alternative to the numeric value for assistive technology.</summary>
    public string? ValueText { get; set; }

    /// <summary>Formats the current value for assistive technology, including in uncontrolled mode.</summary>
    public Func<double, string?>? FormatValueText { get; set; }

    public bool IsEnabled { get; set; } = true;

    protected override Node Build()
    {
        var root = Apply(new UIButton { Role = Role.Slider, Disabled = !IsEnabled }, "aw-slider");
        var parts = _slider.Attach(root, Minimum, Maximum, SmallChange, Value, DefaultValue, ValueChanged, ValueText, FormatValueText);
        parts.Track.Attributes["part"] = "track";
        parts.Range.Attributes["part"] = "range";
        parts.Thumb.Attributes["part"] = "thumb";

        // The track's bounds are the pointer scale, so the fill and thumb use it as their containing block.
        parts.Track.Style = "position: relative";
        parts.Range.Style = "position: absolute; top: 0; bottom: 0; left: 0; pointer-events: none; " + parts.Range.Style.Text;
        parts.Thumb.Style = "position: absolute; top: 50%; " + parts.Thumb.Style.Text;
        UseEffect(_slider.CommitAvailability, root.Disabled);
        return root;
    }
}
