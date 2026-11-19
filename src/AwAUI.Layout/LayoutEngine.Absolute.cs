using System;
using System.Runtime.CompilerServices;
using static AwAUI.Layout.LayoutMath;

namespace AwAUI.Layout;

public sealed partial class LayoutEngine
{
    const int ModeAbs = 1, ModeFixed = 2;

    /// <summary>
    /// 节点 <paramref name="p"/> 是包含块（已定位或根）：布局所有以它为包含块的绝对定位后代。
    /// 只沿「子树中有逃逸的绝对定位后代」标记向下查找，干净子树不被遍历。
    /// 父容器在自己的完整布局中已为每个绝对定位子节点记录了静态位置。
    /// </summary>
    void LayoutOutOfFlow(LayoutNode p, ref float extentR, ref float extentB)
    {
        int mode = ReferenceEquals(p, _root) ? ModeAbs | ModeFixed : ModeAbs;
        WalkOutOfFlow(p, p, 0, 0, mode, ref extentR, ref extentB);
    }

    void WalkOutOfFlow(LayoutNode p, LayoutNode x, float ox, float oy, int mode, ref float extentR, ref float extentB)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var list = RentList();
        CollectChildren(x, list, false);
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var cs = c.Style;
            EnsureEscapeFlags(c);
            if (cs.IsOutOfFlow)
            {
                bool isFixed = cs.Position == Position.Fixed;
                if ((mode & (isFixed ? ModeFixed : ModeAbs)) != 0)
                {
                    // 立即布局，之后再向它内部查找逃逸的 fixed 后代，这样用到的是它的最新位置
                    LayoutAbsolute(p, c, ox, oy);
                    // fixed 元素相对视口定位、不随视口滚动，不计入根（文档）的可滚动溢出（与浏览器一致）；
                    // absolute 仍然计入
                    if (!isFixed)
                    {
                        float r = 0, b = 0;
                        AccumulateExtent(c, ref r, ref b);
                        extentR = Math.Max(extentR, r + ox);
                        extentB = Math.Max(extentB, b + oy);
                    }
                }
                if ((mode & ModeFixed) != 0 && c._escFixed)
                    WalkOutOfFlow(p, c, ox + c._x, oy + c._y, ModeFixed, ref extentR, ref extentB);
            }
            else
            {
                int sub = cs.Position == Position.Static ? mode : mode & ModeFixed;
                if (((sub & ModeAbs) != 0 && c._escAbs) || ((sub & ModeFixed) != 0 && c._escFixed))
                    WalkOutOfFlow(p, c, ox + c._x, oy + c._y, sub, ref extentR, ref extentB);
            }
        }
        ReturnList(list);
    }

    /// <summary>
    /// 结构性标记（与布局结果无关，随 MarkDirty 失效）：子树中是否有越过本节点向上寻找包含块的
    /// absolute（本节点未定位时）/ fixed 后代。
    /// </summary>
    void EnsureEscapeFlags(LayoutNode n)
    {
        if (n._escValid) return;
        RuntimeHelpers.EnsureSufficientExecutionStack();
        bool abs = false, fix = false;
        if (n.Measure is null)
        {
            var list = RentList();
            CollectChildren(n, list, false);
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var pos = c.Style.Position;
                if (pos == Position.Fixed) fix = true;
                else if (pos == Position.Absolute) abs = true;
                EnsureEscapeFlags(c);
                abs |= c._escAbs;
                fix |= c._escFixed;
            }
            ReturnList(list);
        }
        n._escAbs = abs && n.Style.Position == Position.Static;
        n._escFixed = fix;
        n._escValid = true;
    }

    /// <summary>
    /// 布局一个绝对定位节点 <paramref name="a"/>。(<paramref name="ox"/>, <paramref name="oy"/>) 是它的盒父节点
    /// 相对包含块节点 <paramref name="p"/> 边框盒的偏移。
    /// </summary>
    void LayoutAbsolute(LayoutNode p, LayoutNode a, float ox, float oy)
    {
        var s = a.Style;

        // 包含块矩形（p 的边框盒坐标）：已定位祖先的内边距盒，或视口（初始包含块）
        float cbX, cbY, cbW, cbH;
        bool useViewport = ReferenceEquals(p, _root) && (s.Position == Position.Fixed || p.Style.Position == Position.Static);
        if (s.Position == Position.Absolute && ReferenceEquals(a._gridCbOwner, p) && ReferenceEquals(a.Parent, p) && !useViewport)
        {
            // 网格容器是包含块且子节点给出了网格放置属性：包含块是对应的网格区域（CSS Grid 1 §9.1），由网格布局记录
            cbX = a._gridCbX;
            cbY = a._gridCbY;
            cbW = a._gridCbW;
            cbH = a._gridCbH;
        }
        else if (useViewport)
        {
            cbX = -p._x;
            cbY = -p._y;
            cbW = IsDef(_viewportW) ? _viewportW : p._w + p._margin.Horizontal;
            cbH = IsDef(_viewportH) ? _viewportH : p._h + p._margin.Vertical;
        }
        else
        {
            cbX = p._border.Left;
            cbY = p._border.Top;
            cbW = Math.Max(0, p._w - p._border.Horizontal);
            cbH = Math.Max(0, p._h - p._border.Vertical);
        }
        bool rtl = p.Style.Direction == Direction.Rtl;

        var m = ResolveMargin(s, cbW, out int autos);
        var pad = ResolvePadding(s, cbW);
        var bor = ResolveBorder(s);
        float pbW = pad.Horizontal + bor.Horizontal, pbH = pad.Vertical + bor.Vertical;
        ResolveSizes(a, cbW, cbH, pbW, pbH, out var sz);

        var inset = s.Inset;
        float l = inset.Left.Resolve(cbW), r = inset.Right.Resolve(cbW);
        float t = inset.Top.Resolve(cbH), b = inset.Bottom.Resolve(cbH);

        float w = sz.ClampedWidth, h = sz.ClampedHeight;
        // 两侧 inset 都确定且尺寸为 auto：拉伸填满（替换元素除外：auto 尺寸取固有尺寸，CSS 2.1 §10.3.8、§10.6.5）
        if (!IsDef(w) && s.Width.IsAuto && IsDef(l) && IsDef(r) && !s.IsReplaced)
            w = Clamp(cbW - l - r - m.Horizontal, sz.MinWidth, sz.MaxWidth);
        if (!IsDef(h) && s.Height.IsAuto && IsDef(t) && IsDef(b) && !s.IsReplaced)
            h = Clamp(cbH - t - b - m.Vertical, sz.MinHeight, sz.MaxHeight);
        // 高度由宽高比推出且适用自动最小尺寸时交给节点自己的算法（它会取内容高度与比例高度的较大者）
        if (RatioDerivedHeight(s, ref w, ref h, pbW, pbH, in sz, float.NaN) && RatioAutoMin(s)) h = float.NaN;

        if (!IsDef(w) || !IsDef(h))
        {
            // 收缩适应：可用空间为「inset 修正后的包含块」（css-position-3 §4.1）：确定的 inset 收窄对应一侧；
            // 水平两侧 inset 都是 auto 时，起始侧（ltr 的左、rtl 的右）取静态位置，所以从静态位置到包含块另一侧
            float availW = cbW - Z(l) - Z(r);
            if (!IsDef(l) && !IsDef(r))
            {
                float sx = ox + a._staticX - cbX;
                if (a._staticAlignX == AlignStart) availW = cbW - sx;
                else if (a._staticAlignX == AlignEnd) availW = sx + a._staticW;
            }
            var o = MeasureNode(a, w, h, cbW, cbH,
                AvailableSpace.Definite(availW), AvailableSpace.Definite(cbH - Z(t) - Z(b)), SizingMode.Inherent);
            w = o.Width;
            h = o.Height;
        }

        float ml = m.Left, mr = m.Right, mt = m.Top, mb = m.Bottom;
        if ((autos & (AutoLeft | AutoRight)) != 0 && IsDef(l) && IsDef(r))
        {
            float free = cbW - l - r - w - ml - mr;
            if ((autos & AutoLeft) != 0 && (autos & AutoRight) != 0)
            {
                if (free >= 0) { ml += free / 2; mr += free / 2; }
                else if (rtl) ml += free;
                else mr += free;
            }
            else if ((autos & AutoLeft) != 0) ml += free;
            else mr += free;
        }
        if ((autos & (AutoTop | AutoBottom)) != 0 && IsDef(t) && IsDef(b))
        {
            float free = cbH - t - b - h - mt - mb;
            if ((autos & AutoTop) != 0 && (autos & AutoBottom) != 0)
            {
                if (free >= 0) { mt += free / 2; mb += free / 2; }
                else mb += free;
            }
            else if ((autos & AutoTop) != 0) mt += free;
            else mb += free;
        }

        // 包含块坐标中的边框盒位置；过度约束时 ltr 忽略 right、rtl 忽略 left，纵向忽略 bottom
        float x;
        if (!rtl && IsDef(l)) x = l + ml;
        else if (IsDef(r)) x = cbW - r - mr - w;
        else if (IsDef(l)) x = l + ml;
        else x = StaticPosition(ox + a._staticX - cbX, a._staticW, a._staticAlignX, w, ml, mr);

        float y;
        if (IsDef(t)) y = t + mt;
        else if (IsDef(b)) y = cbH - b - mb - h;
        else y = StaticPosition(oy + a._staticY - cbY, a._staticH, a._staticAlignY, h, mt, mb);

        var input = new LayoutInput
        {
            KnownWidth = w,
            KnownHeight = h,
            ParentWidth = cbW,
            ParentHeight = cbH,
            AvailableWidth = AvailableSpace.Definite(cbW),
            AvailableHeight = AvailableSpace.Definite(cbH),
            Mode = RunMode.PerformLayout,
            Sizing = SizingMode.Inherent,
        };
        ComputeNode(a, input);
        a._x = cbX + x - ox;
        a._y = cbY + y - oy;
        a._margin = new BoxEdges(ml, mt, mr, mb);
    }

    /// <summary>外边距盒在静态位置矩形 [start, start+extent] 中按对齐方式放置，返回边框盒起点。</summary>
    static float StaticPosition(float start, float extent, byte align, float size, float marginStart, float marginEnd) => align switch
    {
        AlignEnd => start + extent - marginEnd - size,
        AlignCenter => start + (extent - (size + marginStart + marginEnd)) / 2 + marginStart,
        _ => start + marginStart,
    };
}
