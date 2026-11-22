using System;
using System.Collections.Generic;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>一个吸附点：在某个轴上的目标逻辑偏移，以及它是否声明了 <c>scroll-snap-stop: always</c>。</summary>
internal readonly struct SnapPoint
{
    public SnapPoint(double position, bool always)
    {
        Position = position;
        Always = always;
    }

    public double Position { get; }

    public bool Always { get; }
}

/// <summary>
/// <c>scroll-snap-*</c> 的几何计算（design/21「滚动物理 / 吸附」）：发现滚动容器的吸附区域，把每个区域的
/// <c>scroll-snap-align</c> 换算成该轴上的目标逻辑偏移，并根据 <c>scroll-snap-type</c> 选出最近的有效吸附点。
/// <para>
/// 只处理本框架支持的轴：横向（行内轴，对应 <c>x</c> / <c>inline</c>）与纵向（块轴，对应 <c>y</c> / <c>block</c>）；
/// 没有 writing-mode 支持，所以 <c>block</c> 恒为纵向、<c>inline</c> 恒为横向。吸附区域是「最近的可滚动祖先正是本容器」的后代，
/// 定位 / 粘性 / 在包含块之外的元素按它们的边框盒参与。横向目标按控制器的逻辑偏移约定换算（RTL 从右边缘量起）。
/// </para>
/// </summary>
internal static class ScrollSnap
{
    /// <summary>proximity 吸附的距离阈值：视口在该轴上尺寸的这一比例（另见 <see cref="MinProximityPixels"/>）。UA 自定的值。</summary>
    public const double ProximityThresholdRatio = 0.5;

    /// <summary>proximity 阈值的最小值（逻辑像素），避免很小的视口过度吸附。</summary>
    public const double MinProximityPixels = 24;

    /// <summary>与目标距离小于它时视为已经在吸附点上，不再启动动画（逻辑像素）。</summary>
    public const double PositionTolerance = 0.5;

    /// <summary>容器在 <paramref name="axis"/> 上是否启用吸附；<paramref name="mandatory"/> 区分 mandatory / proximity。</summary>
    public static bool Enabled(ScrollController c, int axis, out bool mandatory)
    {
        mandatory = false;
        if (c._element?.Render?.Style is not { } s) return false;
        var type = s.ScrollSnapType;
        if (!AxisApplies(type.Axis, axis)) return false;
        mandatory = type.Mandatory;
        return true;
    }

    static bool AxisApplies(CssScrollSnapAxis axis, int scrollAxis) => axis switch
    {
        CssScrollSnapAxis.X => scrollAxis == 0,
        CssScrollSnapAxis.Inline => scrollAxis == 0,
        CssScrollSnapAxis.Y => scrollAxis == 1,
        CssScrollSnapAxis.Block => scrollAxis == 1,
        CssScrollSnapAxis.Both => true,
        _ => false,
    };

    /// <summary>
    /// 收集 <paramref name="c"/> 在 <paramref name="axis"/> 上的吸附点。调用时几何必须已经反映了已施加的偏移
    /// （本帧的布局 / 几何阶段之后，或上一帧末）。遍历是线性的。
    /// </summary>
    public static void Collect(ScrollController c, int axis, RenderPipeline pipeline, List<SnapPoint> into)
    {
        into.Clear();
        if (c._element is not { IsAttached: true } scroller || scroller.Render is not { HasBox: true }) return;
        var viewport = ScrollCss.WithScrollPadding(scroller, pipeline.ScrollViewportRect(scroller));
        if (viewport.IsEmpty) return;
        var stack = new Stack<Element>();
        var children = scroller.Children;
        for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            var box = e.Render;
            if (box is not null && box.Style is { } s)
            {
                if (s.Display == CssDisplay.None) continue; // 不渲染，也不进入它的子树
                if (NearestScrollContainerIs(e, scroller))
                {
                    var align = axis == 0 ? s.ScrollSnapAlign.Inline : s.ScrollSnapAlign.Block;
                    if (align != CssScrollSnapAlignment.None && box.HasBox)
                    {
                        var rect = e.BoundingRect;
                        if (!rect.IsEmpty)
                        {
                            double position = axis == 0
                                ? HorizontalTarget(c, rect, viewport, align)
                                : VerticalTarget(c, rect, viewport, align);
                            into.Add(new SnapPoint(c.Clamp(position, axis), s.ScrollSnapStop == CssScrollSnapStop.Always));
                        }
                    }
                }
            }
            var gc = e.Children;
            for (int i = gc.Count - 1; i >= 0; i--) stack.Push(gc[i]);
        }
    }

    /// <summary>最近的可滚动祖先是 <paramref name="scroller"/>（嵌套滚动容器的后代属于嵌套容器，不属于外层）。</summary>
    static bool NearestScrollContainerIs(Element e, Element scroller)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, scroller)) return true;
            if (p._scroll is not null && RenderPipeline.ScrollAxes(p) != 0) return false;
        }
        return false;
    }

    static double VerticalTarget(ScrollController c, Rect rect, Rect viewport, CssScrollSnapAlignment align) =>
        c._appliedY + Delta(rect.Top, rect.Bottom, viewport.Top, viewport.Bottom, align);

    static double HorizontalTarget(ScrollController c, Rect rect, Rect viewport, CssScrollSnapAlignment align)
    {
        // 与 ScrollIntoView 相同的换算：先得到物理平移量，RTL 再换回从右边缘量起的逻辑偏移
        double x = c._appliedX + Delta(rect.Left, rect.Right, viewport.Left, viewport.Right, align);
        return c._rtl ? -x : x;
    }

    static double Delta(float lo, float hi, float vlo, float vhi, CssScrollSnapAlignment align) => align switch
    {
        CssScrollSnapAlignment.Start => lo - vlo,
        CssScrollSnapAlignment.End => hi - vhi,
        CssScrollSnapAlignment.Center => (lo + hi) / 2 - (vlo + vhi) / 2,
        _ => 0,
    };

    /// <summary>
    /// 距离 <paramref name="current"/> 最近的吸附点。mandatory 总是返回（除非已在点上）；proximity 只在最近的点的距离不超过
    /// <c>max(MinProximityPixels, 视口 × ProximityThresholdRatio)</c> 时返回。没有可吸附的点或已经在点上时返回 null。
    /// </summary>
    public static double? Nearest(List<SnapPoint> points, double current, bool mandatory, double viewport)
    {
        if (points.Count == 0) return null;
        double best = 0, bestDistance = double.PositiveInfinity;
        foreach (var p in points)
        {
            double d = Math.Abs(p.Position - current);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = p.Position;
            }
        }
        if (bestDistance <= PositionTolerance) return null;
        if (!mandatory)
        {
            double threshold = Math.Max(MinProximityPixels, viewport * ProximityThresholdRatio);
            if (bestDistance > threshold) return null;
        }
        return best;
    }

    /// <summary>
    /// <c>scroll-snap-stop: always</c>：沿 <paramref name="velocity"/> 的方向，返回第一个会被越过的 always 吸附点；
    /// 惯性模拟应被它截住，不能一次滑过多个吸附点。没有这样的点（或速度为 0）时返回 null。
    /// </summary>
    public static double? FirstAlwaysStop(List<SnapPoint> points, double current, double velocity)
    {
        if (velocity == 0 || double.IsNaN(velocity) || double.IsInfinity(velocity)) return null;
        double? cap = null;
        foreach (var p in points)
        {
            if (!p.Always) continue;
            if (velocity > 0 && p.Position > current + PositionTolerance)
                cap = cap is { } v ? Math.Min(v, p.Position) : p.Position;
            else if (velocity < 0 && p.Position < current - PositionTolerance)
                cap = cap is { } v ? Math.Max(v, p.Position) : p.Position;
        }
        return cap;
    }
}

/// <summary>
/// 把一段惯性模拟在吸附点处截断（<c>scroll-snap-stop: always</c>）：位置不越过 <c>cap</c>，到达后速度归零并结束。
/// </summary>
internal sealed class SnapStopSimulation : Simulation
{
    readonly Simulation _inner;
    readonly double _cap;
    readonly bool _increasing;

    public SnapStopSimulation(Simulation inner, double cap, bool increasing)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cap = cap;
        _increasing = increasing;
    }

    public override double Position(double seconds)
    {
        double p = _inner.Position(seconds);
        return _increasing ? Math.Min(p, _cap) : Math.Max(p, _cap);
    }

    public override double Velocity(double seconds)
    {
        if (Reached(seconds)) return 0;
        return _inner.Velocity(seconds);
    }

    public override bool IsDone(double seconds) => Reached(seconds) || _inner.IsDone(seconds);

    bool Reached(double seconds)
    {
        double p = _inner.Position(seconds);
        return _increasing ? p >= _cap : p <= _cap;
    }
}
