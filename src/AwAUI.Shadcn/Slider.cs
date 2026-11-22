using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>A horizontal slider with one value or a range of values, one focus stop per thumb.</summary>
public sealed partial class Slider : Component
{
    const string RootClass = "group/slider relative flex h-6 w-full min-w-0 touch-none items-center border-0 bg-transparent px-2 py-0 select-none outline-none data-[disabled]:opacity-50 disabled:cursor-not-allowed disabled:opacity-50";
    const string TrackClass = "relative h-1.5 w-full grow overflow-hidden rounded-full bg-muted";
    const string RangeClass = "pointer-events-none absolute inset-y-0 left-0 rounded-full bg-primary";
    const string ThumbClass = "absolute top-1/2 block size-4 shrink-0 rounded-full border border-primary bg-white shadow-sm ring-ring/50 transition-[color,box-shadow] hover:ring-4 focus-visible:ring-4 focus-visible:outline-hidden group-focus-visible/slider:ring-4 group-focus-visible/slider:outline-hidden";

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

    /// <summary>
    /// Controlled values for a multi-thumb slider. Leave null to use <see cref="Value"/> first,
    /// then <see cref="DefaultValues"/>; the value list renders one thumb per entry.
    /// </summary>
    public IReadOnlyList<double>? Values { get; set; }

    /// <summary>Initial uncontrolled values. Takes precedence over <see cref="DefaultValue"/> when it is provided.</summary>
    public IReadOnlyList<double>? DefaultValues { get; set; }

    /// <summary>Receives range value changes. Each thumb moves independently and never crosses its neighbours.</summary>
    public Action<IReadOnlyList<double>>? ValuesChanged { get; set; }
    public Action<IReadOnlyList<double>>? OnValuesChanged { get; set; }

    /// <summary>
    /// The minimum distance between two thumbs, measured in steps. For example 1 with a step of 5 keeps
    /// adjacent thumbs at least 5 apart.
    /// </summary>
    public int? MinStepsBetweenThumbs { get; set; }

    /// <summary>Optional text alternative to the numeric value.</summary>
    public string? ValueText { get; set; }

    /// <summary>Formats the current value for assistive technology, including in uncontrolled mode.</summary>
    public Func<double, string?>? FormatValueText { get; set; }

    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        Values is not null || DefaultValues is not null ? BuildRange() : BuildSingle();

    Node BuildSingle()
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

    Node BuildRange()
    {
        var root = new Div { Id = Id, Ref = Ref };
        root.MergeAttributes(Rest);
        root.Class = Tailwind.TailwindMerge.Cn(RootClass, root.Class.Text, Class);

        var parts = _slider.AttachRange(root, Min, Max, Step, Values, DefaultValues,
            ValuesChanged + OnValuesChanged, MinStepsBetweenThumbs, ValueText, FormatValueText, Disabled);
        parts.Track.Class = TrackClass;
        parts.Range.Class = RangeClass;
        foreach (var thumb in parts.Thumbs) thumb.Class = ThumbClass;
        UseEffect(() => _slider.CommitRangeAvailability(Disabled), Disabled);
        return root;
    }
}
