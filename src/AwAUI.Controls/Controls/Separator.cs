using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>
/// A horizontal or vertical divider. It exposes the separator role and <c>aria-orientation</c>; the orientation also
/// drives the default size through CSS.
/// </summary>
public sealed partial class Separator : Control
{
    protected override string GlobalStyleSheet => SeparatorStyles.StyleSheet;

    /// <summary>Horizontal separators divide rows; vertical separators divide columns. Defaults to horizontal.</summary>
    public Orientation Orientation { get; set; } = Orientation.Horizontal;

    protected override Node Build()
    {
        bool vertical = Orientation == Orientation.Vertical;
        var root = Apply(new Div { Role = Role.Separator }, "aw-separator");
        root.AriaLabel = AriaLabel;
        root.Attributes["data-orientation"] = vertical ? "vertical" : "horizontal";
        root.Attributes["aria-orientation"] = vertical ? "vertical" : "horizontal";
        return root;
    }
}

static class SeparatorStyles
{
    internal const string StyleSheet = """
        @layer aw-separator-defaults {
          :where(.aw-separator) {
            flex: 0 0 auto;
            align-self: stretch;
            background: currentColor;
            opacity: 0.2;
          }
          :where(.aw-separator[data-orientation="horizontal"]) {
            width: 100%;
            height: 1px;
          }
          :where(.aw-separator[data-orientation="vertical"]) {
            width: 1px;
            min-height: 1em;
          }
        }
        """;
}
