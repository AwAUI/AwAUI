using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>Two panes separated by a draggable bar, with a fraction of the space given to the first pane.</summary>
/// <remarks>
/// <see cref="Orientation.Horizontal"/> places the panes side by side with a vertical bar; <see cref="Orientation.Vertical"/>
/// stacks them with a horizontal bar. Give the splitter a size through CSS or its parent. Drag the bar with the pointer, or
/// focus it and use the arrow keys (Shift for a larger step), Home, and End. Both panes keep at least <see cref="MinSize"/>
/// logical pixels when there is room.
/// </remarks>
public sealed partial class GridSplitter : Control
{
    /// <summary>Exactly two panes, first then second.</summary>
    public NodeList? Children { get; set; }

    public Orientation Orientation { get; set; } = Orientation.Horizontal;

    /// <summary>Fraction of the space given to the first pane (0 to 1). Null uses internal state.</summary>
    public double? SplitPosition { get; set; }

    public double DefaultSplitPosition { get; set; } = 0.5;

    /// <summary>Requests a change to <see cref="SplitPosition"/> as the bar is dragged or moved by the keyboard.</summary>
    public Action<double>? SplitPositionChanged { get; set; }

    /// <summary>Minimum size of each pane, in logical pixels.</summary>
    public double MinSize { get; set; } = 64;

    /// <summary>Thickness of the draggable bar, in logical pixels.</summary>
    public double SplitterSize { get; set; } = 6;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Accessible name of the separator.</summary>
    public string? SplitterAriaLabel { get; set; }

    protected override Node Build()
    {
        if (Children is not { Count: 2 } panes)
            throw new InvalidOperationException("GridSplitter requires exactly two panes.");
        return new ResizableSplit
        {
            BaseClass = "aw-grid-splitter",
            Orientation = Orientation,
            Position = SplitPosition,
            PositionChanged = SplitPositionChanged,
            MinSize = MinSize,
            SplitterSize = SplitterSize,
            IsEnabled = IsEnabled,
            SplitterAriaLabel = SplitterAriaLabel,
            First = panes[0],
            Second = panes[1],
            Id = Id,
            Class = Class,
            Style = Style,
            Ref = Ref,
            AriaLabel = AriaLabel,
            Attributes = Attributes,
        };
    }
}
