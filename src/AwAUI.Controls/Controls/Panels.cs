using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>Places children along a single axis, with a gap in logical pixels.</summary>
public sealed partial class StackPanel : Control
{
    public Orientation Orientation { get; set; } = Orientation.Vertical;
    public double Spacing { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Apply(new Div
    {
        Children = Children ?? [],
        Style = (Orientation == Orientation.Horizontal ? "display:flex;flex-direction:row;" : "display:flex;flex-direction:column;")
            + PanelStyles.Gap(Spacing),
    }, "aw-stack-panel");
}

/// <summary>Places children in rows or columns which wrap when they run out of room.</summary>
public sealed partial class WrapPanel : Control
{
    public Orientation Orientation { get; set; }
    public double Spacing { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Apply(new Div
    {
        Children = Children ?? [],
        Style = (Orientation == Orientation.Horizontal
            ? "display:flex;flex-direction:row;flex-wrap:wrap;"
            : "display:flex;flex-direction:column;flex-wrap:wrap;") + PanelStyles.Gap(Spacing),
    }, "aw-wrap-panel");
}

/// <summary>A scrollable content host with optional access to the scroll controller.</summary>
public sealed partial class ScrollViewer : ContentControl
{
    public bool HorizontalScroll { get; set; }
    public bool VerticalScroll { get; set; } = true;
    public ScrollController? ScrollController { get; set; }

    protected override Node BuildTemplate() => Apply(new Div
    {
        Children = BuildContent(),
        ScrollController = ScrollController,
        Style = (HorizontalScroll ? "overflow-x:auto;" : "overflow-x:hidden;")
            + (VerticalScroll ? "overflow-y:auto;" : "overflow-y:hidden;"),
    }, "aw-scroll-viewer");
}

static class PanelStyles
{
    internal static string Gap(double value) => "gap:" +
        (value > 0 && !double.IsInfinity(value) ? value : 0).ToString("0.###", CultureInfo.InvariantCulture) + "px";
}

/// <summary>Arranges children using CSS grid track definitions. Set child placement through its grid style.</summary>
public sealed partial class Grid : Control
{
    public string? ColumnDefinitions { get; set; }
    public string? RowDefinitions { get; set; }
    public double ColumnSpacing { get; set; }
    public double RowSpacing { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Apply(new Div
    {
        Children = Children ?? [],
        Style = "display:grid;column-" + PanelStyles.Gap(ColumnSpacing) + ";row-" + PanelStyles.Gap(RowSpacing)
            + Tracks("columns", ColumnDefinitions) + Tracks("rows", RowDefinitions),
    }, "aw-grid");

    static string Tracks(string axis, string? definitions) =>
        string.IsNullOrWhiteSpace(definitions) ? "" : ";grid-template-" + axis + ":" + definitions;
}

/// <summary>Arranges children in equal width columns and equal height rows.</summary>
public sealed partial class UniformGrid : Control
{
    public int Columns { get; set; } = 1;
    public double Spacing { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Apply(new Div
    {
        Children = Children ?? [],
        Style = "display:grid;grid-auto-rows:1fr;grid-template-columns:repeat(" +
            Math.Max(1, Columns).ToString(CultureInfo.InvariantCulture) + ",minmax(0,1fr));" + PanelStyles.Gap(Spacing),
    }, "aw-uniform-grid");
}
