using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>A horizontal slider with one value and one focus stop.</summary>
public sealed partial class Slider : Component
{
    const string RootClass = "group/slider relative flex h-6 w-full min-w-0 touch-none items-center border-0 bg-transparent px-2 py-0 select-none outline-none disabled:cursor-not-allowed disabled:opacity-50";
    const string TrackClass = "relative h-1.5 w-full rounded-full bg-muted";
    const string RangeClass = "pointer-events-none absolute inset-y-0 left-0 rounded-full bg-primary";
    const string ThumbClass = "absolute top-1/2 block size-4 rounded-full border border-primary bg-background shadow-sm group-focus-visible/slider:ring-[3px] group-focus-visible/slider:ring-ring/50";

    readonly SliderBehavior _slider = new();

    public string? Id { get; set; }
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double Step { get; set; } = 1;

    /// <summary>Controlled value. Leave null to let the slider keep its own value.</summary>
    public double? Value { get; set; }

    /// <summary>Initial uncontrolled value. Defaults to <see cref="Min"/>.</summary>
    public double? DefaultValue { get; set; }

    /// <summary>Receives value changes and supports <c>@bind-Value</c>.</summary>
    public Action<double>? ValueChanged { get; set; }
    public Action<double>? OnValueChange { get; set; }

    /// <summary>Optional text alternative to the numeric value.</summary>
    public string? ValueText { get; set; }

    /// <summary>Formats the current value for assistive technology, including in uncontrolled mode.</summary>
    public Func<double, string?>? FormatValueText { get; set; }

    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var root = new UIButton { Id = Id, Ref = Ref, Role = Role.Slider, Disabled = Disabled };
        root.MergeAttributes(Rest);
        root.Class = Tailwind.TailwindMerge.Cn(RootClass, root.Class.Text, Class);

        var parts = _slider.Attach(root, Min, Max, Step, Value, DefaultValue, ValueChanged + OnValueChange, ValueText, FormatValueText);
        parts.Track.Class = TrackClass;
        parts.Range.Class = RangeClass;
        parts.Thumb.Class = ThumbClass;
        UseEffect(_slider.CommitAvailability, root.Disabled);
        return root;
    }
}
