using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>The pointer control at the top of the popup. Hidden when the options fit.</summary>
public sealed partial class SelectScrollUpButton : Component
{
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => SelectScrollButton.Build(Use<SelectScrollContext>(), true, Children, Class, Rest);
}

/// <summary>The pointer control at the bottom of the popup. Hidden when the options fit.</summary>
public sealed partial class SelectScrollDownButton : Component
{
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => SelectScrollButton.Build(Use<SelectScrollContext>(), false, Children, Class, Rest);
}

internal static class SelectScrollButton
{
    internal static Node Build(SelectScrollContext scroll, bool up, NodeList? children, string? classes, AttributeDictionary? rest)
    {
        var (canUp, canDown) = scroll.Directions.Value;
        if (!canUp && !canDown) return new Fragment();
        bool enabled = up ? canUp : canDown;
        var node = SelectParts.Style(new Div
        {
            Role = Role.Presentation,
            Children = children ?? [SelectParts.Glyph(up ? SelectParts.ChevronUp : SelectParts.ChevronDown,
                "select-scroll-icon", "pointer-events-none size-4")],
        }, up ? "select-scroll-up-button" : "select-scroll-down-button",
            "flex shrink-0 cursor-default items-center justify-center py-1 data-[disabled]:opacity-50", classes, rest);
        node.Attributes["aria-hidden"] = "true";
        node.Attributes["data-disabled"] = enabled ? null : "";
        node.OnPointerDown = EventComposition.Chain(node.OnPointerDown, e => e.PreventDefault());
        node.OnClick = EventComposition.Compose(node.OnClick, e =>
        {
            if (enabled && !e.DefaultPrevented) scroll.Move(up);
        });
        return node;
    }
}
