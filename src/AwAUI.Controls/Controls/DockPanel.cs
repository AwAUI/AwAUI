using System;
using System.Runtime.CompilerServices;
using System.Text;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>The edge of the remaining space at which a child is placed.</summary>
public enum Dock : byte
{
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>Docks children in declaration order. By default, the last child fills the remaining space.</summary>
/// <remarks>
/// Use <c>data-dock="top"</c>, <c>"bottom"</c>, <c>"left"</c>, or <c>"right"</c> on a direct child in markup,
/// or use <see cref="SetDock{T}"/> when constructing a child in C#. Undocked children default to <see cref="Dock.Left"/>.
/// The horizontal edges follow CSS grid direction: left is inline start and right is inline end.
/// </remarks>
public sealed partial class DockPanel : Control
{
    sealed class DockValue
    {
        internal Dock Value;
    }

    static readonly ConditionalWeakTable<Node, DockValue> s_docks = new();

    public NodeList? Children { get; set; }

    /// <summary>Whether the final child fills the remaining space instead of using its dock edge.</summary>
    public bool LastChildFill { get; set; } = true;

    /// <summary>Sets the edge on a child description before rendering and returns that child for composition.</summary>
    public static T SetDock<T>(T child, Dock dock) where T : Node
    {
        if (child is null) throw new ArgumentNullException(nameof(child));
        if (dock > Dock.Bottom) throw new ArgumentOutOfRangeException(nameof(dock));
        s_docks.GetValue(child, static _ => new DockValue()).Value = dock;
        return child;
    }

    /// <summary>Reads the assigned edge, or left when no edge was assigned.</summary>
    public static Dock GetDock(Node child)
    {
        if (child is null) throw new ArgumentNullException(nameof(child));
        if (s_docks.TryGetValue(child, out var dock)) return dock.Value;
        return PanelChild.Attribute(child, "data-dock") switch
        {
            Dock value when value <= Dock.Bottom => value,
            string text when Enum.TryParse<Dock>(text, ignoreCase: true, out var value) && value <= Dock.Bottom => value,
            _ => Dock.Left,
        };
    }

    protected override Node Build()
    {
        var panel = new Div { Style = "display:grid;" };
        if (Children is not { Count: > 0 } children) return Apply(panel, "aw-dock-panel");

        var edges = new Dock[LastChildFill ? children.Count - 1 : children.Count];
        int leftTracks = 0, topTracks = 0, rightTracks = 0, bottomTracks = 0;
        for (int i = 0; i < edges.Length; i++)
        {
            switch (edges[i] = GetDock(children[i]))
            {
                case Dock.Left: leftTracks++; break;
                case Dock.Top: topTracks++; break;
                case Dock.Right: rightTracks++; break;
                case Dock.Bottom: bottomTracks++; break;
            }
        }

        panel.Style = "display:grid;grid-template-columns:" + Tracks(leftTracks, rightTracks)
            + ";grid-template-rows:" + Tracks(topTracks, bottomTracks) + ";";

        // Each edge takes one track from the remaining rectangle. Keep the elements in declaration
        // order so docking and resizing do not reorder focus or reparent keyed children.
        int left = 1, top = 1, right = leftTracks + rightTracks + 2, bottom = topTracks + bottomTracks + 2;
        for (int i = 0; i < children.Count; i++)
        {
            int x0 = left, x1 = right, y0 = top, y1 = bottom;
            if (i < edges.Length)
            {
                switch (edges[i])
                {
                    case Dock.Left: x1 = ++left; break;
                    case Dock.Top: y1 = ++top; break;
                    case Dock.Right: x0 = --right; break;
                    case Dock.Bottom: y0 = --bottom; break;
                }
            }
            var style = FormattableString.Invariant($"display:grid;min-width:0;min-height:0;grid-column:{x0}/{x1};grid-row:{y0}/{y1};");
            panel.Children.Add(PanelChild.Wrap(children[i], style));
        }
        return Apply(panel, "aw-dock-panel");
    }

    static string Tracks(int before, int after)
    {
        // A spanning item contributes to the flexible track in its cross axis, leaving the other
        // dock tracks at their own intrinsic sizes. Without a fill child the center remains empty.
        var tracks = new StringBuilder();
        for (int i = 0; i < before; i++) tracks.Append("max-content ");
        tracks.Append("minmax(0,1fr)");
        for (int i = 0; i < after; i++) tracks.Append(" max-content");
        return tracks.ToString();
    }
}

static class PanelChild
{
    internal static object? Attribute(Node child, string name) => child switch
    {
        ElementNode element => element.Attributes[name],
        Control control => control.Attributes?[name],
        _ => null,
    };

    internal static Div Wrap(Node child, string style)
    {
        var item = new Div
        {
            // The reconciliation key was scoped to a node type before it became a Div.
            Key = child.Key is { } key ? (child.GetType(), key) : null,
            Style = style,
            Children = [child],
        };
        item.Attributes["part"] = "item";
        return item;
    }
}
