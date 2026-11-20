using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>Positions children at offsets from the canvas edges, in logical pixels.</summary>
/// <remarks>
/// Use <c>data-canvas-left</c>, <c>data-canvas-top</c>, <c>data-canvas-right</c>, or <c>data-canvas-bottom</c>
/// on a direct child in markup, or use <see cref="SetPosition{T}"/> in C#. Attributes take numbers without a unit.
/// When neither edge of an axis is set, the child starts at the top or left edge. Left and top take precedence over
/// right and bottom. Children do not contribute to the canvas size; size the canvas through CSS or its parent.
/// </remarks>
public sealed partial class Canvas : Control
{
    sealed class Offsets
    {
        internal double? Left, Top, Right, Bottom;
    }

    static readonly ConditionalWeakTable<Node, Offsets> s_positions = new();

    public NodeList? Children { get; set; }

    /// <summary>Sets all offsets on a child description before rendering and returns that child for composition.</summary>
    /// <param name="child">The child description to position.</param>
    /// <param name="left">Offset from the left edge.</param>
    /// <param name="top">Offset from the top edge.</param>
    /// <param name="right">Offset from the right edge, used when left is unset.</param>
    /// <param name="bottom">Offset from the bottom edge, used when top is unset.</param>
    public static T SetPosition<T>(T child, double? left = null, double? top = null, double? right = null, double? bottom = null)
        where T : Node
    {
        if (child is null) throw new ArgumentNullException(nameof(child));
        CheckOffset(left, nameof(left));
        CheckOffset(top, nameof(top));
        CheckOffset(right, nameof(right));
        CheckOffset(bottom, nameof(bottom));
        var offsets = s_positions.GetValue(child, static _ => new Offsets());
        (offsets.Left, offsets.Top, offsets.Right, offsets.Bottom) = (left, top, right, bottom);
        return child;
    }

    protected override Node Build()
    {
        var panel = new Div { Style = "position:relative;" };
        if (Children is { } children)
        {
            foreach (var child in children)
            {
                double? left, top, right, bottom;
                if (s_positions.TryGetValue(child, out var offsets))
                {
                    left = offsets.Left;
                    top = offsets.Top;
                    right = offsets.Right;
                    bottom = offsets.Bottom;
                }
                else
                {
                    left = ReadOffset(child, "data-canvas-left");
                    top = ReadOffset(child, "data-canvas-top");
                    right = ReadOffset(child, "data-canvas-right");
                    bottom = ReadOffset(child, "data-canvas-bottom");
                }
                var horizontal = left is null && right is { } r ? Inset("right", r) : Inset("left", left ?? 0);
                var vertical = top is null && bottom is { } b ? Inset("bottom", b) : Inset("top", top ?? 0);
                panel.Children.Add(PanelChild.Wrap(child, "position:absolute;" + horizontal + vertical));
            }
        }
        return Apply(panel, "aw-canvas");
    }

    static string Inset(string edge, double value) => edge + ":" + value.ToString("G", CultureInfo.InvariantCulture) + "px;";

    static double? ReadOffset(Node child, string name) =>
        double.TryParse(Convert.ToString(PanelChild.Attribute(child, name), CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && ValidOffset(value) ? value : null;

    static void CheckOffset(double? value, string name)
    {
        if (value is { } offset && !ValidOffset(offset))
            throw new ArgumentOutOfRangeException(name, "A canvas offset must be a finite logical pixel length.");
    }

    static bool ValidOffset(double value) => value >= -float.MaxValue && value <= float.MaxValue;
}
