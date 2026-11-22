using System;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>A single choice among a set of radio items.</summary>
public sealed partial class RadioGroup : Component
{
    readonly TabsFocus _focus = new();
    readonly ControllableState<string> _value = new(StringComparer.Ordinal);

    public string? Value { get; set; }
    public string DefaultValue { get; set; } = "";
    public Action<string>? ValueChanged { get; set; }
    public Action<string>? OnValueChange { get; set; }
    public bool Disabled { get; set; }
    public bool Required { get; set; }
    public bool Loop { get; set; } = true;
    public Orientation? Orientation { get; set; }
    public Direction? Dir { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        string value = _value.Sync(Value, DefaultValue, ValueChanged + OnValueChange);
        var root = TabsParts.Style(new Div { Children = Children ?? [] }, "radio-group", "grid gap-3", Class, Rest);
        root.Attributes["role"] = "radiogroup";
        root.Attributes["aria-required"] = TabsParts.Bool(Required);
        root.Attributes["aria-disabled"] = Disabled ? "true" : null;
        root.Attributes["aria-orientation"] = Orientation is { } axis ? TabsParts.Axis(axis) : null;
        TabsParts.DirectionAttribute(root, Dir);
        return Provide(new Scope(value, Disabled, Orientation, Dir, Loop, _focus, _value.Set), root);
    }

    internal sealed record Scope(string Value, bool Disabled, Orientation? Orientation, Direction? Direction,
        bool Loop, TabsFocus Focus, Action<string> Select);
}

public sealed partial class RadioGroupItem : Component
{
    readonly TabsFocus.Item _item = new();
    readonly Ref<Element> _element = new();
    const string ItemClass = "inline-flex aspect-square size-4 shrink-0 items-center justify-center rounded-full border border-input bg-transparent p-0 text-primary shadow-xs transition-[color,box-shadow] outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:bg-input/30 dark:aria-invalid:ring-destructive/40";

    public string Value { get; set; } = "";
    public string? Id { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = Use<RadioGroup.Scope>();
        string value = Value;
        bool selected = value.Length > 0 && value == group.Value;
        var button = TabsParts.Style(new UIButton { Id = Id, Disabled = group.Disabled || Disabled, Role = Role.Radio, Ref = _element },
            "radio-group-item", ItemClass, Class, Rest);
        button.Ref = _element;
        button.TabIndex = group.Focus.GetTabIndex(_item, selected);
        button.Attributes["aria-checked"] = TabsParts.Bool(selected);
        button.Attributes["data-state"] = selected ? "checked" : "unchecked";
        button.Attributes["data-disabled"] = button.Disabled ? "" : null;
        if (selected)
        {
            // Match the shadcn DOM: an indicator wrapper holding the filled dot. AwAUI 的 Span 是行内文本片段、
            // 不参与盒子布局，所以这里用 Div 承载尺寸（视觉与官方的 span 相同）。
            var indicator = TabsParts.Style(new Div(), "radio-group-indicator",
                "relative flex size-4 items-center justify-center", null, null);
            indicator.Attributes["aria-hidden"] = "true";
            indicator.Attributes["data-state"] = "checked";
            indicator.Children.Add(new Div { Class = "pointer-events-none block size-2 rounded-full bg-primary" });
            button.Children.Add(indicator);
        }
        void Select()
        {
            if (value.Length > 0) group.Select(value);
        }
        TabsParts.Activate(button, Select);
        group.Focus.Wire(button, _item, group.Orientation, group.Direction, group.Loop, onKeyboardMove: Select);
        UseEffect(() => group.Focus.Register(_item, _element.Current, selected), group.Focus, selected, button.Disabled);
        return button;
    }
}
