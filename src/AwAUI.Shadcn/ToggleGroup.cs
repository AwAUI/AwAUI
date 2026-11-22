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

    /// <summary>
    /// Space between items, in Tailwind spacing units. The default (0) joins the items into one segmented control,
    /// matching shadcn/ui; use <c>Spacing = 2</c> for separate buttons.
    /// </summary>
    public int Spacing { get; set; }

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        string single = _single.Sync(Value, DefaultValue, ValueChanged);
        var many = _multiple.Sync(Values, DefaultValues, ValuesChanged);
        var root = TabsParts.Style(new Div { Role = Role.Group, Children = Children ?? [] }, "toggle-group",
            "group/toggle-group flex w-fit items-center rounded-md data-[orientation=vertical]:flex-col data-[spacing=default]:data-[variant=outline]:shadow-xs",
            Class, Rest);
        root.Attributes["data-variant"] = ToggleStyles.VariantName(Variant);
        root.Attributes["data-size"] = ToggleStyles.SizeName(Size);
        root.Attributes["data-spacing"] = Spacing.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Spacing > 0)
            root.Style = (string.IsNullOrEmpty(root.Style.Text) ? "" : root.Style.Text + ";") +
                "gap: " + (Spacing * 4).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
        root.Attributes["data-orientation"] = TabsParts.Axis(Orientation);
        TabsParts.DirectionAttribute(root, Dir);
        return Provide(new Scope(Multiple, single, many, Disabled, Size, Variant, Spacing, Orientation, Dir, Loop, _focus, Toggle), root);
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
        ToggleSize Size, ToggleVariant Variant, int Spacing, Orientation Orientation, Direction? Direction, bool Loop,
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
        string spacing = group.Spacing.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string itemClass = ToggleStyles.Base + " " + ToggleStyles.Variant(group.Variant) + " " + ToggleStyles.Size(group.Size) +
            " w-auto min-w-0 shrink-0 px-3 focus:z-10 focus-visible:z-10" +
            " data-[spacing=0]:rounded-none data-[spacing=0]:shadow-none" +
            " data-[spacing=0]:first:rounded-l-md data-[spacing=0]:last:rounded-r-md" +
            " data-[spacing=0]:data-[variant=outline]:border-l-0 data-[spacing=0]:data-[variant=outline]:first:border-l";
        var button = TabsParts.Style(new UIButton { Ref = _element, Role = Role.Button,
            Disabled = Disabled || group.Disabled, Children = Children ?? [] }, "toggle-group-item",
            itemClass, Class, Rest);
        button.Ref = _element;
        button.TabIndex = group.Focus.GetTabIndex(_item, pressed);
        button.Attributes["data-variant"] = ToggleStyles.VariantName(group.Variant);
        button.Attributes["data-size"] = ToggleStyles.SizeName(group.Size);
        button.Attributes["data-spacing"] = spacing;
        button.Attributes["aria-pressed"] = TabsParts.Bool(pressed);
        button.Attributes["data-state"] = pressed ? "on" : "off";
        button.Attributes["data-disabled"] = button.Disabled ? "" : null;
        TabsParts.Activate(button, () => group.Toggle(value));
        group.Focus.Wire(button, _item, group.Orientation, group.Direction, group.Loop);
        UseEffect(() => group.Focus.Register(_item, _element.Current, pressed), group.Focus, pressed, button.Disabled);
        return button;
    }
}
