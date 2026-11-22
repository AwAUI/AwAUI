using System;
using System.Globalization;
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
/// and auto height, so give the child a width if it should wrap. The transform is applied after layout; in
/// <see cref="ViewboxStretch.Uniform"/> mode the natural content size is also reported to the parent as an
/// <c>aspect-ratio</c>, so an auto-sized axis derives the scaled desired size instead of the unscaled content height.
/// Use in-flow content to establish the source size. Both stretch modes clip ordinary content at the viewport.
/// Style the outer control or the child; the viewport and content parts own the fit geometry.
/// </remarks>
public sealed partial class Viewbox : Decorator
{
    readonly Ref<Element> _box = new();
    readonly Ref<Element> _viewport = new();
    readonly Ref<Element> _content = new();
    (float X, float Y, float Scale) _appliedFit;
    string? _appliedRatio;

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
            Ref = _box,
            Style = "display:flex;flex-direction:column;align-items:stretch;box-sizing:border-box;min-width:0;min-height:0;",
            Children = [viewport],
        };
        box.Attributes["data-stretch"] = uniform ? "uniform" : "none";
        Apply(box, "aw-viewbox");
        // 调用方通过 Ref 指向外层时由它持有引用；否则用内部的引用，供布局完成后写入宽高比
        box.Ref = Ref ?? _box;
        return box;
    }

    Element? BoxElement => _box.Current ?? Ref?.Current;

    protected override void OnMount()
    {
        if (Root is { } root) root.LayoutCompleted += FitContent;
    }

    protected override void OnUnmount()
    {
        if (Root is { } root) root.LayoutCompleted -= FitContent;
        _appliedFit = default;
        _appliedRatio = null;
    }

    void FitContent(UIRoot _)
    {
        bool uniform = Stretch == ViewboxStretch.Uniform;
        if (_content.Current is not { } content || _viewport.Current is not { } viewport || !content.IsRendered)
            return;
        if (!uniform)
        {
            // 从 Uniform 切回 None 时撤销比例与缩放；文本自然尺寸不变，不需要重建
            BoxElement?.SetStyleOverride("aspect-ratio", null);
            if (_appliedFit != default) content.SetStyleOverride("transform", null);
            (_appliedFit, _appliedRatio) = (default, null);
            return;
        }

        var natural = content.BoundingRect;
        // 父布局据此得到缩放后的期望尺寸：高度为 auto 时由宽度按自然宽高比推出，父节点无需再显式给高度。
        // 两个轴都确定时 aspect-ratio 不参与计算，fit 仍取两轴的最小缩放。
        string? ratio = natural.IsFinite && natural.Width > 0 && natural.Height > 0
            ? (natural.Width / natural.Height).ToString("R", CultureInfo.InvariantCulture)
            : null;
        if (ratio != _appliedRatio)
        {
            BoxElement?.SetStyleOverride("aspect-ratio", ratio);
            _appliedRatio = ratio;
        }

        // BoundingRect excludes both this fit and any ancestor transforms. The engine applies the override
        // before paint, so mount and resize need no visibility toggle or deferred component rebuild.
        var fit = Fit(viewport.BoundingRect, natural);
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
