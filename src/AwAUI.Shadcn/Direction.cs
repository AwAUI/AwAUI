using AwAUI.Layout;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>
/// Sets the writing direction for its subtree through a <c>dir</c> attribute. The wrapper keeps
/// <c>display: contents</c> so it does not affect layout, and it stays mounted even when <see cref="Dir"/> is null so
/// the children keep their identity.
/// </summary>
public sealed partial class DirectionProvider : Component
{
    /// <summary>Writing direction of the subtree. Null leaves the inherited direction untouched.</summary>
    public Direction? Dir { get; set; }

    public string? Class { get; set; }

    public NodeList? Children { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var wrapper = StaticParts.Style(new Div { Children = Children ?? new NodeList() },
            "direction-provider", "contents", Class, Rest);
        TabsParts.DirectionAttribute(wrapper, Dir);
        return wrapper;
    }
}
