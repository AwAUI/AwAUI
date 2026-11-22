using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.Svg;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UISvg = AwAUI.UI.Svg;

namespace AwAUI.Shadcn;

/// <summary>Shared builders for the shadcn S2 structural components (breadcrumb, pagination, empty, item, groups, field).</summary>
internal static class StaticParts
{
    /// <summary>
    /// Merges the unmatched attributes, the default classes and the caller's classes onto <paramref name="node"/> and sets its part slot.
    /// Mirrors <c>TabsParts.Style</c>: explicit classes win over spread attributes.
    /// </summary>
    internal static T Style<T>(T node, string slot, string? defaults, string? classes, AttributeDictionary? rest)
        where T : ElementNode
    {
        node.MergeAttributes(rest);
        node.Class = AwAUI.Tailwind.TailwindMerge.Cn(defaults, node.Class.Text, classes);
        node.Attributes["data-slot"] = slot;
        return node;
    }

    /// <summary>A decorative Lucide glyph (hidden from assistive technology).</summary>
    internal static UISvg Glyph(SvgDocument document, string? classes = null)
    {
        var glyph = new UISvg(document) { Class = classes };
        glyph.Attributes["aria-hidden"] = "true";
        return glyph;
    }

    /// <summary>
    /// Builds a keyboard-focusable link. AwAUI has no native anchor with focus and disabled semantics, so the native button element
    /// carries <see cref="Role.Link"/> (the same approach as <c>HyperlinkButton</c>). Space is left to the caller (links activate with Enter).
    /// </summary>
    internal static UIButton Link(string slot, string? defaults, string? href, bool disabled, string? classes,
        AttributeDictionary? rest, NodeList? children, Action<PointerEvent>? onClick, Ref<Element>? @ref)
    {
        var link = new UIButton
        {
            Role = Role.Link,
            Disabled = disabled,
            Ref = @ref,
            OnClick = onClick,
            Children = children ?? new NodeList(),
        };
        Style(link, slot, defaults, classes, rest);
        link.Attributes["href"] ??= href;
        if (link.Disabled) link.Attributes["aria-disabled"] = "true";
        link.OnKeyDown = EventComposition.Compose(link.OnKeyDown, e =>
        {
            if (e.PhysicalKey == PhysicalKey.Space && ReferenceEquals(e.Target, e.CurrentTarget)) e.PreventDefault();
        });
        return link;
    }

    /// <summary>Element node with an arbitrary tag (nav, ol, li, fieldset, legend, p ...).</summary>
    internal sealed class TagElement(string tag) : ElementNode
    {
        protected override Element CreateElement() => new(tag);
    }
}
