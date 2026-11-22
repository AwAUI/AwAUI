using System;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Layout;
using AwAUI.Platform;

namespace AwAUI.UI;

public sealed partial class RenderPipeline
{
    /// <summary>
    /// 计算 <c>position: sticky</c> 元素相对其静态边框盒的视觉偏移（根空间逻辑像素），design/08。
    /// <para>
    /// sticky 盒在布局中与 static 完全相同（占据静态空间，见 <see cref="Position.Sticky"/>），本方法只在绝对几何阶段
    /// 依据最近的滚动容器当前的偏移求视觉位置；滚动只改变这个偏移，不触发重排 / 样式重算。
    /// </para>
    /// <para>
    /// 每个轴：把外边距盒夹到「粘性视图矩形」（滚动容器视口内缩 inset）中，再夹到包含块内（元素随包含块的末端边缘移出）。
    /// 只有起始 / 只有结束 inset 时盒只向该方向移动。物理 top/right/bottom/left；RTL 下的逻辑 inset 未做（见 design/08）。
    /// </para>
    /// </summary>
    /// <param name="staticBox">未施加粘性偏移的静态边框盒（根空间）。</param>
    internal void ComputeStickyOffset(Element e, LayoutNode node, in Rect staticBox, out float dx, out float dy)
    {
        dx = dy = 0;
        if (node.Style.Position != Position.Sticky) return;
        var cs = e.Render?.Style;
        if (cs is null) return;
        bool anyX = !cs.Left.IsAuto || !cs.Right.IsAuto;
        bool anyY = !cs.Top.IsAuto || !cs.Bottom.IsAuto;
        if (!anyX && !anyY) return; // 没有 inset：与 static 相同（CSS position:sticky 的初始值）

        var cb = StickyContainingBlock(e);
        var vp = StickyScrollport(e);
        var m = node.Margin;

        if (anyX)
        {
            float l = InsetPx(cs.Left, cb.Width), r = InsetPx(cs.Right, cb.Width);
            dx = StickyAxis(staticBox.X, staticBox.Width, m.Left, m.Right, vp.Left, vp.Right, l, r, cb.Left, cb.Right);
        }
        if (anyY)
        {
            float t = InsetPx(cs.Top, cb.Height), b = InsetPx(cs.Bottom, cb.Height);
            dy = StickyAxis(staticBox.Y, staticBox.Height, m.Top, m.Bottom, vp.Top, vp.Bottom, t, b, cb.Top, cb.Bottom);
        }
    }

    /// <summary><c>top/right/bottom/left</c> 的计算值按包含块尺寸解析；auto 返回 NaN（视为未指定）。</summary>
    static float InsetPx(SizeValue v, float basis) => v.IsLengthPercentage ? v.Value.Resolve(basis) : float.NaN;

    /// <summary>
    /// 单轴上的粘性偏移：返回边框盒相对静态位置的位移。
    /// <paramref name="start"/>/<paramref name="end"/> 是两端 inset（NaN 表示 auto），<paramref name="viewStart"/>/<paramref name="viewEnd"/>
    /// 是滚动容器视口的起止边，<paramref name="cbStart"/>/<paramref name="cbEnd"/> 是包含块的起止边（由 UI 层传入的可能是滚动内容范围）。
    /// </summary>
    static float StickyAxis(float borderStart, float borderSize, float marginStart, float marginEnd,
        float viewStart, float viewEnd, float start, float end, float cbStart, float cbEnd)
    {
        bool hasStart = !float.IsNaN(start), hasEnd = !float.IsNaN(end);
        if (!hasStart && !hasEnd) return 0;
        // 约束的是外边距盒：inset 从滚动容器视口量到外边距边缘
        float boxStart = borderStart - marginStart;
        float boxSize = borderSize + marginStart + marginEnd;
        float result;
        if (hasStart && hasEnd)
        {
            float lo = viewStart + start;             // 粘性视图矩形的起始边
            float hi = viewEnd - end - boxSize;       // 盒起点能到达的最远结束边
            // 盒比粘性视图矩形还大时无法同时满足两端：按规范起始 inset 优先
            result = lo > hi ? lo : Math.Max(lo, Math.Min(boxStart, hi));
        }
        else if (hasStart)
        {
            result = Math.Max(boxStart, viewStart + start);
        }
        else
        {
            result = Math.Min(boxStart, viewEnd - end - boxSize);
        }
        // 包含块约束：到达包含块的末端后随它一起移出（包含块退化时不约束）
        if (cbEnd > cbStart)
        {
            float max = Math.Max(cbStart, cbEnd - boxSize);
            if (result < cbStart) result = cbStart;
            else if (result > max) result = max;
        }
        return result + marginStart - borderStart;
    }

    /// <summary>
    /// sticky 的包含块（根空间）：最近的块容器祖先的内容盒（跳过 <c>display: contents</c> 与尚未布局的盒）。
    /// 根元素同时是文档滚动容器时，包含块取滚动内容范围——浏览器里 html / body 的高度随内容增长（design/08 的限制说明）。
    /// </summary>
    Rect StickyContainingBlock(Element e)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
        {
            var rb = p.Render;
            var node = rb?.Layout;
            if (rb is null || node is null || !rb.HasBox || rb.Kind != BoxKind.Container) continue;
            if (rb.Style is { Display: CssDisplay.Contents }) continue;
            if (p.Parent is null)
            {
                // 根元素：文档内容可以溢出它的内容盒，包含块取滚动内容范围（至少视口大小）
                var ss = node.ScrollSize;
                return new Rect(0, 0, Math.Max(_viewport.Width, ss.Width), Math.Max(_viewport.Height, ss.Height));
            }
            var c = node.ContentBox;
            return new Rect(rb.Border.X + c.X, rb.Border.Y + c.Y, c.Width, c.Height);
        }
        return default;
    }

    /// <summary>
    /// sticky 的滚动容器视口（根空间）：最近的滚动容器祖先的内边距盒；没有时是窗口视口。
    /// 根元素总是视口（即使 <c>overflow: visible</c>）。视口本身不随自己的滚动移动，其绝对位置已含更外层滚动容器的平移。
    /// </summary>
    Rect StickyScrollport(Element e)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
        {
            if (p.Parent is null) break; // 根元素：窗口视口
            if (ScrollAxes(p) != 0 && p.Render is { HasBox: true })
                return ScrollViewportRect(p);
        }
        return new Rect(0, 0, _viewport.Width, _viewport.Height);
    }
}
