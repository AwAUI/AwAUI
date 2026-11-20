using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>A plain block that hosts content and accepts the common control styles and attributes.</summary>
public partial class Decorator : Control
{
    /// <summary>Optional single child for code composition. Takes precedence over <see cref="Children"/>.</summary>
    public Node? Child { get; set; }

    /// <summary>Content supplied as ordinary markup children.</summary>
    public NodeList? Children { get; set; }

    protected NodeList BuildChildren() => Child is { } child ? [child] : Children ?? [];

    protected override Node Build() => Apply(new Div { Children = BuildChildren() }, "aw-decorator");
}
