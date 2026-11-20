using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>A set of pressed buttons supporting either single or multiple selection.</summary>
public sealed partial class ToggleGroup : Component
{
    readonly TabsFocus _focus = new();
    readonly ControllableState<string> _single = new(StringComparer.Ordinal);
    readonly ControllableState<IReadOnlyList<string>> _multiple = new();

    public bool Multiple { get; set; }
    public string? Value { get; set; }
    public string DefaultValue { get; set; } = "";
    public Action<string>? ValueChanged { get; set; }
    public IReadOnlyList<string>? Values { get; set; }
    public IReadOnlyList<string> DefaultValues { get; set; } = Array.Empty<string>();
    public Action<IReadOnlyList<string>>? ValuesChanged { get; set; }
    public bool Disabled { get; set; }
    public bool Loop { get; set; } = true;
    public Orientation Orientation { get; set; }
    public Direction? Dir { get; set; }
    public ToggleSize Size { get; set; }
    public ToggleVariant Variant { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        string single = _single.Sync(Value, DefaultValue, ValueChanged);
        var many = _multiple.Sync(Values, DefaultValues, ValuesChanged);
        var root = TabsParts.Style(new Div { Role = Role.Group, Children = Children ?? [] }, "toggle-group",
            "inline-flex w-fit items-center gap-1 data-[orientation=vertical]:flex-col", Class, Rest);
        root.Attributes["data-orientation"] = TabsParts.Axis(Orientation);
        TabsParts.DirectionAttribute(root, Dir);
        return Provide(new Scope(Multiple, single, many, Disabled, Size, Variant, Orientation, Dir, Loop, _focus, Toggle), root);
    }

    void Toggle(string value)
    {
        if (value.Length == 0) return;
        if (!Multiple)
        {
            _single.Set(_single.Value == value ? "" : value);
            return;
        }
        var values = _multiple.Value;
        _multiple.Set(values.Contains(value, StringComparer.Ordinal)
            ? values.Where(x => x != value).ToArray()
            : values.Concat(new[] { value }).ToArray());
    }

    internal sealed record Scope(bool Multiple, string Value, IReadOnlyList<string> Values, bool Disabled,
        ToggleSize Size, ToggleVariant Variant, Orientation Orientation, Direction? Direction, bool Loop,
        TabsFocus Focus, Action<string> Toggle);
}

public sealed partial class ToggleGroupItem : Component
{
    readonly TabsFocus.Item _item = new();
    readonly Ref<Element> _element = new();

    public string Value { get; set; } = "";
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = Use<ToggleGroup.Scope>();
        string value = Value;
        bool pressed = value.Length > 0 && (group.Multiple ? group.Values.Contains(value, StringComparer.Ordinal) : group.Value == value);
        string size = group.Size switch
        {
            ToggleSize.Sm => "h-8 px-1.5", ToggleSize.Lg => "h-10 px-2.5", _ => "h-9 px-2",
        };
        string variant = group.Variant == ToggleVariant.Outline ? " border border-input shadow-xs" : "";
        var button = TabsParts.Style(new UIButton { Ref = _element, Role = Role.Button,
            Disabled = Disabled || group.Disabled, Children = Children ?? [] }, "toggle-group-item",
            "inline-flex items-center justify-center rounded-md bg-transparent text-sm font-medium outline-none hover:bg-muted focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[state=on]:bg-accent data-[state=on]:text-accent-foreground disabled:opacity-50 " + size + variant,
            Class, Rest);
        button.Ref = _element;
        button.TabIndex = group.Focus.GetTabIndex(_item, pressed);
        button.Attributes["aria-pressed"] = TabsParts.Bool(pressed);
        button.Attributes["data-state"] = pressed ? "on" : "off";
        TabsParts.Activate(button, () => group.Toggle(value));
        group.Focus.Wire(button, _item, group.Orientation, group.Direction, group.Loop);
        UseEffect(() => group.Focus.Register(_item, _element.Current, pressed), group.Focus, pressed, button.Disabled);
        return button;
    }
}
