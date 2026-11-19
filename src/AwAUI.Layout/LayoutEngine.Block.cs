using System;
using System.Collections.Generic;
using static AwAUI.Layout.LayoutMath;

namespace AwAUI.Layout;

public sealed partial class LayoutEngine
{
    /// <summary>
    /// 简化的块布局（design/08）：块级子节点纵向堆叠；<c>width: auto</c> 的子节点撑满包含块减去外边距（行内级容器与替换元素除外）；
    /// 宽度确定时水平 auto 外边距居中；不做外边距折叠与浮动。
    /// 本节点宽度未由父节点给出时（收缩适应的场景）取 fit-content 宽度。
    /// </summary>
    LayoutOutput ComputeBlock(LayoutNode node, in LayoutInput input)
    {
        var s = node.Style;
        var pad = ResolvePadding(s, input.ParentWidth);
        var bor = ResolveBorder(s);
        float pbW = pad.Horizontal + bor.Horizontal, pbH = pad.Vertical + bor.Vertical;

        StyleSizes sz;
        if (input.Sizing == SizingMode.Inherent) ResolveSizes(node, input.ParentWidth, input.ParentHeight, pbW, pbH, out sz);
        else sz = StyleSizes.Unconstrained(pbW, pbH);

        float w = Or(input.KnownWidth, sz.ClampedWidth);
        float h = Or(input.KnownHeight, sz.ClampedHeight);
        // 高度由宽高比推出：子节点仍以它为确定高度（百分比基准），布局后高度至少为内容高度（非替换元素的自动最小尺寸）
        bool ratioMin = input.Sizing == SizingMode.Inherent && RatioDerivedHeight(s, ref w, ref h, pbW, pbH, in sz, input.KnownHeight) && RatioAutoMin(s);

        bool perform = input.IsPerform;
        if (!perform && IsDef(w) && ((IsDef(h) && !ratioMin) || input.WidthOnly) && !input.RequestBaseline) return new LayoutOutput(w, h, float.NaN);

        var children = RentList();
        CollectChildren(node, children, perform);

        var margin = ResolveMargin(s, input.ParentWidth, out _);
        if (!IsDef(w))
        {
            float content = BlockIntrinsicWidth(children, input.AvailableWidth.Shrink(margin.Horizontal + pbW));
            w = Clamp(content + pbW, sz.MinWidth, sz.MaxWidth);
            if (input.WidthOnly)
            {
                ReturnList(children);
                return new LayoutOutput(w, float.NaN, float.NaN);
            }
        }

        float innerW = Math.Max(0, w - pbW);
        float innerH = IsDef(h) ? Math.Max(0, h - pbH) : float.NaN;
        var availW = AvailableSpace.Definite(innerW);
        var availH = IsDef(innerH) ? AvailableSpace.Definite(innerH) : input.AvailableHeight.Shrink(margin.Vertical + pbH);
        bool rtl = s.Direction == Direction.Rtl;

        float contentLeft = bor.Left + pad.Left, contentTop = bor.Top + pad.Top;
        float y = contentTop;
        float baseline = float.NaN;
        float extentR = 0, extentB = 0;

        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            var cs = c.Style;
            if (cs.IsOutOfFlow)
            {
                if (perform)
                {
                    // 静态位置：当前行的内容盒宽度内，ltr 靠左、rtl 靠右
                    c._staticX = contentLeft;
                    c._staticY = y;
                    c._staticW = innerW;
                    c._staticH = 0;
                    c._staticAlignX = rtl ? AlignEnd : AlignStart;
                    c._staticAlignY = AlignStart;
                }
                continue;
            }

            var cm = ResolveMargin(cs, innerW, out int autos);
            var cpad = ResolvePadding(cs, innerW);
            var cbor = ResolveBorder(cs);
            ResolveSizes(c, innerW, innerH, cpad.Horizontal + cbor.Horizontal, cpad.Vertical + cbor.Vertical, out var csz);

            float cw = csz.ClampedWidth;
            if (!IsDef(cw) && cs.Width.IsAuto && !cs.IsShrinkToFit)
                cw = Clamp(innerW - cm.Horizontal, csz.MinWidth, csz.MaxWidth);

            var ci = new LayoutInput
            {
                KnownWidth = cw,
                KnownHeight = float.NaN,
                ParentWidth = innerW,
                ParentHeight = innerH,
                AvailableWidth = availW,
                AvailableHeight = availH,
                Mode = input.Mode,
                Sizing = SizingMode.Inherent,
                // 块容器的基线取第一个「有基线」的在流子节点：前面的子节点没有基线时要继续向后问，
                // 否则求尺寸（ComputeSize）与完整布局得到的基线会不一致
                RequestBaseline = !perform && input.RequestBaseline && !IsDef(baseline),
            };
            var o = ComputeNode(c, ci);

            float ml = cm.Left, mr = cm.Right;
            bool autoH = (autos & (AutoLeft | AutoRight)) != 0;
            if (autoH)
            {
                float free = Math.Max(0, innerW - o.Width - ml - mr);
                if ((autos & AutoLeft) != 0 && (autos & AutoRight) != 0) { ml += free / 2; mr += free / 2; }
                else if ((autos & AutoLeft) != 0) ml += free;
                else mr += free;
            }
            float x = autoH || !rtl ? contentLeft + ml : contentLeft + innerW - mr - o.Width;
            y += cm.Top;

            if (perform)
            {
                RelativeOffset(cs, innerW, innerH, rtl, out float dx, out float dy);
                c._x = x + dx;
                c._y = y + dy;
                c._margin = new BoxEdges(ml, cm.Top, mr, cm.Bottom);
                AccumulateExtent(c, ref extentR, ref extentB);
            }
            if (!IsDef(baseline) && IsDef(o.Baseline)) baseline = y + o.Baseline;

            y += o.Height + cm.Bottom;
        }

        if (!IsDef(h)) h = Clamp(y - contentTop + pbH, sz.MinHeight, sz.MaxHeight);
        else if (ratioMin) h = Math.Max(h, Math.Min(y - contentTop + pbH, sz.MaxHeight));

        if (perform) FinishContainer(node, w, h, baseline, pad, bor, extentR, extentB);
        ReturnList(children);
        return new LayoutOutput(w, h, baseline);
    }

    /// <summary>块容器内容的固有宽度：子节点外边距盒贡献的最大值；确定可用空间时取 fit-content。</summary>
    float BlockIntrinsicWidth(List<LayoutNode> children, AvailableSpace avail)
    {
        switch (avail.Kind)
        {
            case AvailableSpaceKind.MinContent:
                return MaxBlockContribution(children, AvailableSpace.MinContent);
            case AvailableSpaceKind.MaxContent:
                return MaxBlockContribution(children, AvailableSpace.MaxContent);
            default:
                float maxC = MaxBlockContribution(children, AvailableSpace.MaxContent);
                if (maxC <= avail.Value) return maxC;
                float minC = MaxBlockContribution(children, AvailableSpace.MinContent);
                return Math.Min(maxC, Math.Max(minC, avail.Value));
        }
    }

    float MaxBlockContribution(List<LayoutNode> children, AvailableSpace kind)
    {
        float max = 0;
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            var cs = c.Style;
            if (cs.IsOutOfFlow) continue;
            // 固有尺寸贡献中包含块宽度不确定：百分比外边距按 0、百分比宽度按 auto
            var cm = ResolveMargin(cs, float.NaN, out _);
            var o = MeasureNode(c, float.NaN, float.NaN, float.NaN, float.NaN, kind, AvailableSpace.MaxContent, SizingMode.Inherent,
                widthOnly: true);
            max = Math.Max(max, o.Width + cm.Horizontal);
        }
        return max;
    }

    internal const byte AlignStart = 0, AlignCenter = 1, AlignEnd = 2;
}
