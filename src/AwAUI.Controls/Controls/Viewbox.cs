using System;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>How a <see cref="Viewbox"/> fits content into its viewport.</summary>
public enum ViewboxStretch
{
    /// <summary>Keep the natural size at the viewport's block and inline start.</summary>
    None,

    /// <summary>Scale proportionally to fit both axes, then center the result.</summary>
    Uniform,
}

/// <summary>Fits a subtree of UI into a CSS-sized viewport without reflowing the scaled content.</summary>
/// <remarks>
/// Size the Viewbox with CSS or let its parent allocate both axes. Content is laid out at max-content width
/// and auto height, so give the child a width if it should wrap. The transform is applied after layout;
/// auto sizing still uses ordinary CSS layout and does not report a scaled desired size to the parent.
/// Use in-flow content to establish the source size. Both stretch modes clip ordinary content at the viewport.
/// Style the outer control or the child; the viewport and content parts own the fit geometry.
/// </remarks>
public sealed partial class Viewbox : Decorator
{
    readonly Ref<Element> _viewport = new();
    readonly Ref<Element> _content = new();
    (float X, float Y, float Scale) _appliedFit;

    public ViewboxStretch Stretch { get; set; } = ViewboxStretch.Uniform;

    protected override Node Build()
    {
        bool uniform = Stretch == ViewboxStretch.Uniform;
        var content = new Div
        {
            Ref = _content,
            Style = "display:block;position:relative;width:max-content;height:auto;"
                + "transform-origin:0px 0px;transition-property:none;animation-name:none;"
                // Keep the same stacking context when a Uniform fit crosses its natural size.
                + (uniform ? "transform:scale(1);" : "transform:none;"),
            Children = BuildChildren(),
        };
        content.Attributes["part"] = "content";

        // An in-flow viewport takes the root's content area without counting its padding or border.
        var viewport = new Div
        {
            Ref = _viewport,
            Style = "display:block;position:relative;flex:1 1 auto;min-width:0;min-height:0;overflow:clip;",
            Children = [content],
        };
        viewport.Attributes["part"] = "viewport";

        var box = new Div
        {
            Style = "display:flex;flex-direction:column;align-items:stretch;box-sizing:border-box;min-width:0;min-height:0;",
            Children = [viewport],
        };
        box.Attributes["data-stretch"] = uniform ? "uniform" : "none";
        return Apply(box, "aw-viewbox");
    }

    protected override void OnMount()
    {
        if (Root is { } root) root.LayoutCompleted += FitContent;
    }

    protected override void OnUnmount()
    {
        if (Root is { } root) root.LayoutCompleted -= FitContent;
        _appliedFit = default;
    }

    void FitContent(UIRoot _)
    {
        bool uniform = Stretch == ViewboxStretch.Uniform;
        if ((!uniform && _appliedFit == default) || _content.Current is not { } content
            || _viewport.Current is not { } viewport || !content.IsRendered)
            return;

        // BoundingRect excludes both this fit and any ancestor transforms. The engine applies the override
        // before paint, so mount and resize need no visibility toggle or deferred component rebuild.
        var fit = uniform ? Fit(viewport.BoundingRect, content.BoundingRect) : default;
        if (fit == _appliedFit) return;
        content.SetStyleOverride("transform", fit.Scale == 0 ? null
            : FormattableString.Invariant($"translate({fit.X:R}px,{fit.Y:R}px) scale({fit.Scale:R})"));
        _appliedFit = fit;
    }

    static (float X, float Y, float Scale) Fit(Rect viewport, Rect natural)
    {
        if (viewport.IsEmpty || natural.IsEmpty || !viewport.IsFinite || !natural.IsFinite)
            return default;

        float scale = Math.Min(viewport.Width / natural.Width, viewport.Height / natural.Height);
        if (!(scale > 0 && scale <= float.MaxValue)) return default;

        // The unscaled content may start at the right edge in RTL. Measure the actual offset.
        float x = viewport.X - natural.X + (viewport.Width - natural.Width * scale) / 2;
        float y = viewport.Y - natural.Y + (viewport.Height - natural.Height * scale) / 2;
        if (!(x >= -float.MaxValue && x <= float.MaxValue && y >= -float.MaxValue && y <= float.MaxValue))
            return default;

        return scale == 1 && x == 0 && y == 0 ? default : (x, y, scale);
    }
}
