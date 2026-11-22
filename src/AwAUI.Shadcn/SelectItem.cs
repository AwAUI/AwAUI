using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>An option. Each item in a select needs a unique, nonempty value.</summary>
public sealed partial class SelectItem : Component
{
    const string ItemClass =
        "relative flex w-full cursor-default items-center gap-2 rounded-sm py-1.5 pr-8 pl-2 text-left text-sm outline-hidden select-none scroll-my-1 " +
        "focus:bg-accent focus:text-accent-foreground data-[disabled]:opacity-50 data-[disabled]:pointer-events-none " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4 [&_svg:not([class*='text-'])]:text-muted-foreground";

    public string Value { get; set; } = "";

    /// <summary>Allows an empty <see cref="Value"/> (used by <see cref="NativeSelect"/>'s placeholder option).</summary>
    internal bool AllowEmptyValue { get; set; }
    /// <summary>Overrides typeahead text. Also supplies the label if the item has no text.</summary>
    public string? TextValue { get; set; }
    public bool Disabled { get; set; }
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    internal SelectScope? Scope { get; private set; }

    protected override Node Build()
    {
        if (string.IsNullOrEmpty(Value) && !AllowEmptyValue)
            throw new ArgumentException("A SelectItem needs a nonempty Value.", nameof(Value));
        var ctx = Use<SelectContext>();
        var popup = Use<PopoverPrimitive.Context>().Shared;
        Scope = ctx.Scope;
        bool selected = ctx.Value == Value;

        var text = new Div { Class = "flex min-w-0 items-center gap-2", Children = Children ?? [TextValue ?? Value] };
        text.Attributes["data-slot"] = "select-item-text";
        var button = SelectParts.Style(new UIButton { Role = Role.Option, Disabled = Disabled, TabIndex = -1 },
            "select-item", ItemClass, Class, Rest);
        button.Attributes["type"] = "button";
        button.Attributes["data-state"] = selected ? "checked" : "unchecked";
        button.Attributes["data-disabled"] = button.Disabled ? "" : null;
        button.Attributes["aria-selected"] = SelectParts.Bool(selected);
        button.Attributes["aria-disabled"] = SelectParts.Bool(button.Disabled);
        button.Children.Add(text);
        if (selected)
            button.Children.Add(SelectParts.Glyph(SelectParts.Check, "select-item-indicator", "pointer-events-none absolute right-2 size-4"));

        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (e.DefaultPrevented || !popup.IsOpen || ctx.Disabled || button.Disabled) return;
            ctx.Scope.Selection.Set(Value);
            popup.SetOpen(false);
        });
        button.OnPointerMove = EventComposition.Compose(button.OnPointerMove, e =>
        {
            if (!e.DefaultPrevented && popup.IsOpen && !button.Disabled && e.PointerType == PointerType.Mouse
                && e.CurrentTarget is { IsFocused: false } item)
                item.Focus(new FocusOptions { Origin = FocusOrigin.Pointer, PreventScroll = true });
        });
        button.OnFocus = EventComposition.Chain(button.OnFocus, e => e.CurrentTarget?.SetAttributeOverride("data-highlighted", ""));
        button.OnBlur = EventComposition.Chain(button.OnBlur, e => e.CurrentTarget?.SetAttributeOverride("data-highlighted", null));
        return button;
    }

    internal string LabelOf(Element option)
    {
        var text = option.ElementChildren.Count > 0 ? option.ElementChildren[0].TextContent.Trim() : "";
        return text.Length > 0 ? text : TextValue ?? Value;
    }
}
