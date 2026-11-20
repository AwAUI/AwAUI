using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static AwAUI.Layout.LayoutMath;

namespace AwAUI.Layout;

/// <summary>
/// CSS 布局引擎（design/05、design/08「布局」）：Flexbox（CSS Flexible Box Layout Level 1 §9）、Grid（CSS Grid Layout 1，
/// 不含子网格与基线对齐）、简化的块布局、相对/绝对/固定定位、min-content / max-content 固有尺寸。
/// <para>
/// 内部协议是「约束下传、尺寸上报」：每个节点按（已知尺寸、包含块尺寸、可用空间、模式）计算，结果按该键缓存在节点上，
/// 干净子树在相同约束下的重复布局为 O(1)。单线程使用；实例持有可复用的临时缓冲区，不应并发调用。
/// </para>
/// </summary>
public sealed partial class LayoutEngine
{
    LayoutNode? _root;
    LayoutNode? _lastRoot;
    float _viewportW = float.NaN, _viewportH = float.NaN;
    readonly Stack<List<LayoutNode>> _listPool = new();

    /// <summary>诊断：布局算法实际执行（未命中缓存）的累计次数。</summary>
    internal long AlgorithmRuns;

    /// <summary>以给定视口尺寸布局整棵树。无穷大或 NaN 的轴按 max-content 处理。</summary>
    public void Compute(LayoutNode root, Size availableSpace) =>
        Compute(root, AvailableSpace.Definite(availableSpace.Width), AvailableSpace.Definite(availableSpace.Height));

    /// <summary>
    /// 布局整棵树。<paramref name="root"/> 作为初始包含块（视口，尺寸即可用空间）中的块级盒：
    /// <c>width: auto</c> 时撑满可用宽度，高度由内容决定。没有已定位祖先的绝对/固定定位后代相对视口定位。
    /// </summary>
    public void Compute(LayoutNode root, AvailableSpace width, AvailableSpace height)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        float viewportW = width.IsDefinite ? width.Value : float.NaN;
        float viewportH = height.IsDefinite ? height.Value : float.NaN;
        if (!ReferenceEquals(root, _lastRoot) || !SameFloat(viewportW, _viewportW) || !SameFloat(viewportH, _viewportH))
        {
            // 根身份变化会改变绝对定位后代的归属；视口变化会改变以视口为包含块的定位结果
            // （可用高度不在缓存键中），两种情况都不能沿用根上次的完整布局。子树的缓存不受影响。
            root._hasFinal = false;
            _lastRoot = root;
        }
        _root = root;
        _viewportW = viewportW;
        _viewportH = viewportH;
        try
        {
            var s = root.Style;
            if (s.Display == Display.None)
            {
                HideSubtree(root);
                return;
            }

            float cbW = _viewportW, cbH = _viewportH;
            var margin = ResolveMargin(s, cbW, out _);
            var pad = ResolvePadding(s, cbW);
            var bor = ResolveBorder(s);
            ResolveSizes(root, cbW, cbH, pad.Horizontal + bor.Horizontal, pad.Vertical + bor.Vertical, out var sz);
            float knownW = sz.ClampedWidth;
            if (float.IsNaN(knownW) && s.Width.IsAuto && !s.IsShrinkToFit && width.IsDefinite)
                knownW = Clamp(width.Value - margin.Horizontal, sz.MinWidth, sz.MaxWidth);

            RelativeOffset(s, cbW, cbH, s.Direction == Direction.Rtl, out float dx, out float dy);
            // 根的位置要先于布局确定：根的绝对定位处理需要它来换算视口坐标
            root._x = margin.Left + dx;
            root._y = margin.Top + dy;
            root._margin = margin;

            var input = new LayoutInput
            {
                KnownWidth = knownW,
                KnownHeight = float.NaN,
                ParentWidth = cbW,
                ParentHeight = cbH,
                AvailableWidth = width,
                AvailableHeight = height,
                Mode = RunMode.PerformLayout,
                Sizing = SizingMode.Inherent,
            };
            ComputeNode(root, input);
        }
        finally
        {
            _root = null;
        }
    }

    static bool SameFloat(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));

    // ======================================================================
    // 分派与缓存
    // ======================================================================

    internal LayoutOutput ComputeNode(LayoutNode node, in LayoutInput rawInput)
    {
        // 树深度由应用决定；与其让进程因栈溢出崩溃，不如抛出可捕获的异常
        RuntimeHelpers.EnsureSufficientExecutionStack();

        // 规范化缓存键，去掉不影响结果的部分，避免同一结果占用多个缓存槽：
        // - 某轴尺寸已知时，该轴的可用空间不再被使用；
        // - 横排书写模式下 auto 高度从不依赖可用高度（CSS 中块向尺寸由内容决定），
        //   所以可用高度一律按 max-content 处理（高度已知时叶子测量收到的是确定高度）。
        var input = rawInput;
        if (!float.IsNaN(input.KnownWidth)) input.AvailableWidth = AvailableSpace.MaxContent;
        input.AvailableHeight = AvailableSpace.MaxContent;
        // - 样式不引用包含块尺寸时，包含块尺寸（百分比基准）不影响本节点的结果
        //   （子节点的百分比基准是本节点自己的内容盒，与之无关）。
        if ((node._basisFlags & LayoutNode.UsesParentWidth) == 0) input.ParentWidth = float.NaN;
        if ((node._basisFlags & LayoutNode.UsesParentHeight) == 0) input.ParentHeight = float.NaN;

        var style = node.Style;
        if (style.Display == Display.None)
        {
            if (input.IsPerform) HideSubtree(node);
            return default;
        }

        if (input.WidthOnly)
        {
            // 只求宽度且宽度已知：无需计算
            if (!float.IsNaN(input.KnownWidth)) return new LayoutOutput(input.KnownWidth, float.NaN, float.NaN);
            // 宽度只在叶子测量（如按高度换算宽度的图片）与宽高比中依赖已知高度；其余情况去掉已知高度，
            // 以免祖先各自的拉伸高度为同一个节点派生出许多只差在高度上的缓存键。
            // （代价：非叶子节点求固有宽度时，被拉伸得到的确定高度不再向下传给后代，依赖该高度换算宽度的后代
            //  ——如 height:100% 加 aspect-ratio 的子节点——按高度不确定处理）
            if (node.Measure is null && style.AspectRatio is null) input.KnownHeight = float.NaN;
        }

        if (input.IsPerform)
        {
            if (node._hasFinal && node._finalInput.SameAs(input)) return node._finalOutput;
        }
        else if (node.TryGetCached(input, out var cached))
        {
            return cached;
        }

        AlgorithmRuns++;
        // 完整布局会改写子孙的位置：先作废旧的完整布局缓存，这样算法中途抛出异常（栈深度、测量函数异常）时
        // 不会留下「缓存说已布局、子孙却是半新半旧」的状态
        if (input.IsPerform) node._hasFinal = false;
        LayoutOutput output;
        if (node.Measure is not null) output = ComputeLeaf(node, input);
        else if (style.Display is Display.Flex or Display.InlineFlex) output = ComputeFlex(node, input);
        else if (style.Display is Display.Grid or Display.InlineGrid) output = ComputeGrid(node, input);
        else output = ComputeBlock(node, input);

        if (input.IsPerform)
        {
            node._hasFinal = true;
            node._finalInput = input;
            node._finalOutput = output;
            node._isDirty = false;
            node._hidden = false;
            node._w = output.Width;
            node._h = output.Height;
            node._baseline = output.Baseline;
        }
        else
        {
            node.StoreCache(input, output);
        }
        return output;
    }

    /// <summary>以 ComputeSize 模式计算节点（便捷封装）。</summary>
    LayoutOutput MeasureNode(LayoutNode node, float knownW, float knownH, float parentW, float parentH,
        AvailableSpace availW, AvailableSpace availH, SizingMode sizing, bool requestBaseline = false, bool widthOnly = false)
    {
        var input = new LayoutInput
        {
            KnownWidth = knownW,
            KnownHeight = knownH,
            ParentWidth = parentW,
            ParentHeight = parentH,
            AvailableWidth = availW,
            AvailableHeight = availH,
            Mode = RunMode.ComputeSize,
            Sizing = sizing,
            RequestBaseline = requestBaseline,
            WidthOnly = widthOnly,
        };
        return ComputeNode(node, input);
    }

    // ======================================================================
    // 叶子（测量函数）
    // ======================================================================

    LayoutOutput ComputeLeaf(LayoutNode node, in LayoutInput input)
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
        bool inherent = input.Sizing == SizingMode.Inherent;
        // 高度由宽高比推出时，非替换元素（如文本）的自动最小高度是内容高度（CSS Sizing 4 §5.2）；要测量后才知道是否适用，
        // 见下方 LooksNatural
        bool ratioMin = inherent && RatioDerivedHeight(s, ref w, ref h, pbW, pbH, in sz, input.KnownHeight) && RatioAutoMin(s);

        bool perform = input.IsPerform;
        if (perform)
        {
            var children = node.ChildList;
            for (int i = 0; i < children.Count; i++) HideSubtree(children[i]);
        }
        if (!perform && IsDef(w) && IsDef(h) && !ratioMin && !input.RequestBaseline) return new LayoutOutput(w, h, float.NaN);

        var margin = ResolveMargin(s, input.ParentWidth, out _);
        // 宽度确定时高度只作为可用空间传递（文本只关心宽度），使「求高」与「最终布局」共享同一个测量结果
        var cw = IsDef(w) ? AvailableSpace.Definite(w - pbW) : input.AvailableWidth.Shrink(margin.Horizontal + pbW);
        var ch = IsDef(h) && !IsDef(w) ? AvailableSpace.Definite(h - pbH) : input.AvailableHeight.Shrink(margin.Vertical + pbH);
        var measured = MeasureLeaf(node, new MeasureConstraints(cw, ch));

        if (!IsDef(w))
        {
            if (inherent && !IsDef(h) && UsableRatio(s) is float ratio)
            {
                // 宽高都是 auto 且有宽高比：宽度取内容宽度，并受经比例传递过来的 min/max-height 约束（CSS Sizing 4 §5.2：
                // 比值决定轴之外的那个轴的 min/max 经比例传递到比值决定轴）；高度再由最终宽度按比例推出。
                // 这样替换元素的测量函数不必猜测「确定宽度是可用宽度还是钳制结果」
                // 测量结果是内容盒尺寸：固有宽高比作用于内容盒（与 box-sizing 无关，CSS Sizing 4 §5.1），所以有测量尺寸时
                // 按它的比例在内容盒上传递与推出；测量为空（只有比例、没有固有尺寸）时才按样式比例与 box-sizing
                if (LooksNatural(s, measured))
                {
                    float rc = measured.Width / measured.Height;
                    TransferredWidthLimits(rc, pbW, pbH, pbW, pbH, in sz, out float minW, out float maxW);
                    w = Clamp(measured.Width + pbW, minW, maxW);
                    h = Clamp(Lim((w - pbW) / rc + pbH), sz.MinHeight, sz.MaxHeight);
                }
                else
                {
                    // 只有比例而没有固有尺寸，或非替换的内容（文本）：比值作用于 box-sizing 指定的盒；
                    // 非替换元素的高度不小于内容高度（自动最小尺寸）
                    bool cb = s.BoxSizing == BoxSizing.ContentBox;
                    TransferredWidthLimits(ratio, cb ? pbW : 0, cb ? pbH : 0, pbW, pbH, in sz, out float minW, out float maxW);
                    float natW = measured.Width + pbW;
                    w = Clamp(natW, minW, maxW);
                    h = RatioHeight(s, ratio, w, pbW, pbH, in sz);
                    ratioMin = RatioAutoMin(s) && measured.Width + measured.Height > 0;
                    if (ratioMin && !input.WidthOnly && Math.Abs(w - natW) > 0.001f)
                        measured = MeasureLeaf(node, new MeasureConstraints(AvailableSpace.Definite(w - pbW), ch));
                }
            }
            else
            {
                w = Clamp(measured.Width + pbW, sz.MinWidth, sz.MaxWidth);
                // 宽度被 min/max 改变后，高度要按最终宽度重新测量（文本换行）
                if (!IsDef(h) && !input.WidthOnly && Math.Abs(w - (measured.Width + pbW)) > 0.001f)
                    measured = MeasureLeaf(node, new MeasureConstraints(AvailableSpace.Definite(w - pbW), ch));
            }
        }
        if (!IsDef(h)) h = Clamp(measured.Height + pbH, sz.MinHeight, sz.MaxHeight);
        else if (ratioMin && !LooksNatural(s, measured))
            h = Math.Max(h, Math.Min(measured.Height + pbH, sz.MaxHeight));

        var contentSize = new Size(Math.Max(0, w - pbW), Math.Max(0, h - pbH));
        float baseline = node.Measure!.GetBaseline(node, contentSize);
        // 防御：基线为无穷时按「没有基线」处理（否则基线对齐中 ∞ - ∞ 产生 NaN 位置）；有限值钳制到长度上限
        if (float.IsInfinity(baseline)) baseline = float.NaN;
        if (IsDef(baseline)) baseline = Lim(baseline) + pad.Top + bor.Top;

        if (perform)
        {
            node._padding = pad;
            node._border = bor;
            // 替换元素的内容按使用尺寸缩放绘制，不会溢出；测量结果（固有尺寸）不计入可滚动溢出
            node._scrollW = s.IsReplaced ? Math.Max(0, w - bor.Horizontal) : Math.Max(w - bor.Horizontal, measured.Width + pad.Horizontal);
            node._scrollH = s.IsReplaced ? Math.Max(0, h - bor.Vertical) : Math.Max(h - bor.Vertical, measured.Height + pad.Vertical);
        }
        return new LayoutOutput(w, h, baseline);
    }

    /// <summary>
    /// 叶子的测量结果是否是替换元素的固有尺寸：标记了 <see cref="LayoutStyle.IsReplaced"/> 且测量尺寸非空。是则按测量尺寸的比值
    /// 作用于内容盒、不施加自动最小高度；否则（文本等非替换内容，或只有比例、没有固有尺寸）按样式比值与 box-sizing，
    /// 非替换元素的高度不小于内容高度。
    /// </summary>
    static bool LooksNatural(LayoutStyle s, Size measured) =>
        s.IsReplaced && measured.Width > 0 && measured.Height > 0;

    static Size MeasureLeaf(LayoutNode node, in MeasureConstraints c)
    {
        if (node.TryGetMeasured(c, out var size)) return size;
        size = node.Measure!.Measure(node, c);
        float mw = size.Width, mh = size.Height;
        // 防御：测量函数返回 NaN、负数或无穷时按 0 处理
        if (!(mw >= 0) || float.IsInfinity(mw)) mw = 0;
        if (!(mh >= 0) || float.IsInfinity(mh)) mh = 0;
        mw = Lim(mw);
        mh = Lim(mh);
        size = new Size(mw, mh);
        node.StoreMeasured(c, size);
        return size;
    }

    // ======================================================================
    // 共用：样式解析
    // ======================================================================

    static BoxEdges ResolvePadding(LayoutStyle s, float cbW)
    {
        var p = s.Padding;
        return new BoxEdges(
            Math.Max(0, p.Left.ResolveOrZero(cbW)), Math.Max(0, p.Top.ResolveOrZero(cbW)),
            Math.Max(0, p.Right.ResolveOrZero(cbW)), Math.Max(0, p.Bottom.ResolveOrZero(cbW)));
    }

    static BoxEdges ResolveBorder(LayoutStyle s)
    {
        var b = s.Border;
        return new BoxEdges(Pos(b.Left), Pos(b.Top), Pos(b.Right), Pos(b.Bottom));
    }

    static float Pos(float v) => v > 0 && !float.IsInfinity(v) ? Lim(v) : 0;

    internal const int AutoLeft = 1, AutoTop = 2, AutoRight = 4, AutoBottom = 8;

    /// <summary>解析外边距（百分比相对包含块宽度），auto 按 0 返回并记入 <paramref name="autoMask"/>。</summary>
    static BoxEdges ResolveMargin(LayoutStyle s, float cbW, out int autoMask)
    {
        var m = s.Margin;
        autoMask = (m.Left.IsAuto ? AutoLeft : 0) | (m.Top.IsAuto ? AutoTop : 0) |
                   (m.Right.IsAuto ? AutoRight : 0) | (m.Bottom.IsAuto ? AutoBottom : 0);
        return new BoxEdges(Z(m.Left.Resolve(cbW)), Z(m.Top.Resolve(cbW)), Z(m.Right.Resolve(cbW)), Z(m.Bottom.Resolve(cbW)));
    }

    static float Z(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;

    /// <summary>
    /// 解析节点的 width/height/min/max（边框盒尺度，已按 box-sizing 换算并应用 aspect-ratio）。
    /// 固有尺寸关键字通过以 Content 模式计算节点自身求得。
    /// </summary>
    void ResolveSizes(LayoutNode node, float cbW, float cbH, float pbW, float pbH, out StyleSizes r)
    {
        var s = node.Style;
        bool contentBox = s.BoxSizing == BoxSizing.ContentBox;
        float adjW = contentBox ? pbW : 0, adjH = contentBox ? pbH : 0;

        r.HeightFromRatio = false;
        r.Width = SizeValue(node, s.Width, cbW, cbW, cbH, adjW, true);
        r.Height = SizeValue(node, s.Height, cbH, cbW, cbH, adjH, false);
        float minW = SizeValue(node, s.MinWidth, cbW, cbW, cbH, adjW, true);
        float minH = SizeValue(node, s.MinHeight, cbH, cbW, cbH, adjH, false);
        float maxW = SizeValue(node, s.MaxWidth, cbW, cbW, cbH, adjW, true);
        float maxH = SizeValue(node, s.MaxHeight, cbH, cbW, cbH, adjH, false);
        r.MinWidth = IsDef(minW) ? Math.Max(minW, pbW) : pbW;
        r.MinHeight = IsDef(minH) ? Math.Max(minH, pbH) : pbH;
        r.MaxWidth = IsDef(maxW) ? maxW : float.PositiveInfinity;
        r.MaxHeight = IsDef(maxH) ? maxH : float.PositiveInfinity;

        if (s.AspectRatio is float ratio && ratio > 0 && !float.IsInfinity(ratio))
        {
            // 比值推出的轴按另一轴的使用值（经 min/max 钳制后）计算（CSS Sizing 4 §5.1）：
            // <img width=300 height=150 style="max-width: 100%; height: auto"> 在 120px 宽的容器中是 120×60，而不是 120×150
            if (!IsDef(r.Width) && IsDef(r.Height))
                r.Width = Lim((Math.Max(Clamp(r.Height, r.MinHeight, r.MaxHeight), pbH) - adjH) * ratio + adjW);
            else if (IsDef(r.Width) && !IsDef(r.Height))
            {
                r.Height = Lim((Math.Max(Clamp(r.Width, r.MinWidth, r.MaxWidth), pbW) - adjW) / ratio + adjH);
                r.HeightFromRatio = true;
            }
        }
        if (IsDef(r.Width)) r.Width = Math.Max(r.Width, pbW);
        if (IsDef(r.Height)) r.Height = Math.Max(r.Height, pbH);
    }

    float SizeValue(LayoutNode node, Dimension d, float basis, float cbW, float cbH, float adj, bool horizontal)
    {
        switch (d.Kind)
        {
            case DimensionKind.Length:
            case DimensionKind.Percent:
            case DimensionKind.Calc:
                float v = d.Resolve(basis);
                return IsDef(v) && !float.IsInfinity(v) ? Math.Max(0, v) + adj : float.NaN;
            case DimensionKind.MinContent:
            case DimensionKind.MaxContent:
            case DimensionKind.FitContent:
                return horizontal ? IntrinsicWidth(node, d.Kind, cbW, cbH) : float.NaN;
            default:
                return float.NaN;
        }
    }

    /// <summary>节点自身内容决定的边框盒宽度（min-content / max-content / fit-content 关键字）。</summary>
    float IntrinsicWidth(LayoutNode node, DimensionKind kind, float cbW, float cbH)
    {
        var avail = kind switch
        {
            DimensionKind.MinContent => AvailableSpace.MinContent,
            DimensionKind.MaxContent => AvailableSpace.MaxContent,
            _ => IsDef(cbW) ? AvailableSpace.Definite(cbW) : AvailableSpace.MaxContent,
        };
        return MeasureNode(node, float.NaN, float.NaN, cbW, cbH, avail, AvailableSpace.MaxContent, SizingMode.Content,
            widthOnly: true).Width;
    }

    /// <summary>
    /// 一个轴已确定、另一轴未定时按 aspect-ratio 推出另一轴，并钳制到 min/max。
    /// 返回高度是否由宽度推出（调用方据此施加非替换元素的自动最小尺寸，见 <see cref="RatioAutoMin"/>）。
    /// </summary>
    static bool ApplyAspectRatio(LayoutStyle s, ref float w, ref float h, float pbW, float pbH, in StyleSizes sz)
    {
        if (UsableRatio(s) is not float ratio) return false;
        bool contentBox = s.BoxSizing == BoxSizing.ContentBox;
        float adjW = contentBox ? pbW : 0, adjH = contentBox ? pbH : 0;
        if (IsDef(w) && !IsDef(h))
        {
            h = RatioHeight(s, ratio, w, pbW, pbH, in sz);
            return true;
        }
        if (IsDef(h) && !IsDef(w)) w = Clamp(Lim((h - adjH) * ratio + adjW), sz.MinWidth, sz.MaxWidth);
        return false;
    }

    /// <summary>
    /// 应用 <see cref="ApplyAspectRatio"/>，并返回高度是否来自宽高比：由已知宽度推出，或样式尺寸解析时已由确定的 width 推出
    /// （父节点没有给出确定高度时）。
    /// </summary>
    static bool RatioDerivedHeight(LayoutStyle s, ref float w, ref float h, float pbW, float pbH, in StyleSizes sz, float knownH)
    {
        bool derived = ApplyAspectRatio(s, ref w, ref h, pbW, pbH, in sz);
        return derived || (sz.HeightFromRatio && float.IsNaN(knownH));
    }

    static float? UsableRatio(LayoutStyle s) =>
        s.AspectRatio is float ratio && ratio > 0 && !float.IsInfinity(ratio) ? ratio : null;

    /// <summary>由边框盒宽度按比例推出的边框盒高度（作用于 box-sizing 指定的盒），钳制到 min/max-height。</summary>
    static float RatioHeight(LayoutStyle s, float ratio, float w, float pbW, float pbH, in StyleSizes sz)
    {
        bool contentBox = s.BoxSizing == BoxSizing.ContentBox;
        float adjW = contentBox ? pbW : 0, adjH = contentBox ? pbH : 0;
        return Clamp(Lim(Math.Max(0, w - adjW) / ratio + adjH), sz.MinHeight, sz.MaxHeight);
    }

    /// <summary>
    /// 宽高都是 auto 时宽度的有效 min/max：自身的 min/max-width 与经比例传递的 min/max-height 取交集。
    /// 传递过来的最小值不超过自身的 max-width、最大值不小于自身的 min-width（CSS Sizing 4 §5.2）。
    /// </summary>
    /// <param name="adjW">比例作用的盒与边框盒的宽度差（内容盒时为 padding + border，边框盒时为 0）。</param>
    static void TransferredWidthLimits(float ratio, float adjW, float adjH, float pbW, float pbH, in StyleSizes sz, out float minW, out float maxW)
    {
        float tMin = Lim(Math.Max(0, sz.MinHeight - adjH) * ratio + adjW);
        float tMax = float.IsPositiveInfinity(sz.MaxHeight) ? float.PositiveInfinity : Lim(Math.Max(0, sz.MaxHeight - adjH) * ratio + adjW);
        minW = Math.Max(sz.MinWidth, Math.Min(tMin, sz.MaxWidth));
        maxW = Math.Min(sz.MaxWidth, Math.Max(tMax, sz.MinWidth));
    }

    /// <summary>
    /// flex / 网格容器的宽高比：高度由宽度推出且适用自动最小尺寸时，把比例高度作为最小高度、高度按内容决定
    /// （结果为 max(比例高度, 内容高度)，不超过 max-height）。代价是子节点的百分比高度此时按不确定处理。
    /// </summary>
    static void RatioAsMinHeight(LayoutStyle s, ref float w, ref float h, float pbW, float pbH, ref StyleSizes sz, float knownH)
    {
        if (RatioDerivedHeight(s, ref w, ref h, pbW, pbH, in sz, knownH) && RatioAutoMin(s))
        {
            sz.MinHeight = Math.Max(sz.MinHeight, Math.Min(h, sz.MaxHeight));
            h = float.NaN;
        }
    }

    /// <summary>
    /// 高度由宽高比推出时是否有基于内容的自动最小高度（CSS Sizing 4 §5.2 automatic minimum size）：
    /// 非替换元素、<c>min-height: auto</c>、不是滚动容器。
    /// </summary>
    static bool RatioAutoMin(LayoutStyle s) => !s.IsReplaced && s.MinHeight.IsAuto && !s.IsScrollContainer;

    /// <summary>相对定位偏移（百分比相对包含块）。左右都指定时 ltr 取 left、rtl 取 right。</summary>
    static void RelativeOffset(LayoutStyle s, float cbW, float cbH, bool rtl, out float dx, out float dy)
    {
        dx = dy = 0;
        if (s.Position != Position.Relative) return;
        var inset = s.Inset;
        float l = inset.Left.Resolve(cbW), r = inset.Right.Resolve(cbW);
        float t = inset.Top.Resolve(cbH), b = inset.Bottom.Resolve(cbH);
        if (IsDef(l) && (!rtl || !IsDef(r))) dx = l;
        else if (IsDef(r)) dx = -r;
        if (IsDef(t)) dy = t;
        else if (IsDef(b)) dy = -b;
    }

    // ======================================================================
    // 共用：子节点收集、隐藏、临时缓冲
    // ======================================================================

    List<LayoutNode> RentList() => _listPool.Count > 0 ? _listPool.Pop() : new List<LayoutNode>();

    void ReturnList(List<LayoutNode> list)
    {
        list.Clear();
        _listPool.Push(list);
    }

    /// <summary>
    /// 收集参与布局的盒子节点：跳过 display:none（完整布局时把其子树清零），展开 display:contents。
    /// </summary>
    void CollectChildren(LayoutNode node, List<LayoutNode> list, bool perform)
    {
        var children = node.ChildList;
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            var d = c.Style.Display;
            if (d == Display.None)
            {
                if (perform) HideSubtree(c);
                continue;
            }
            if (d == Display.Contents)
            {
                if (perform)
                {
                    c.ZeroLayout();
                    c._isDirty = false;
                    c._hidden = false;
                    c._hasFinal = false;
                }
                // 嵌套的 display:contents 链不经过 ComputeNode，需要单独防栈溢出
                RuntimeHelpers.EnsureSufficientExecutionStack();
                CollectChildren(c, list, perform);
                continue;
            }
            list.Add(c);
        }
    }

    /// <summary>按 <c>order</c> 稳定排序（order 相同保持文档顺序，CSS Display 3「order-modified document order」）。</summary>
    static void SortByOrder(List<LayoutNode> list)
    {
        int n = list.Count;
        bool any = false;
        for (int i = 0; i < n && !any; i++) any = list[i].Style.Order != 0;
        if (!any) return;
        if (n <= 32)
        {
            for (int i = 1; i < n; i++)
            {
                var x = list[i];
                int o = x.Style.Order, j = i - 1;
                while (j >= 0 && list[j].Style.Order > o)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = x;
            }
            return;
        }
        // 键 = (order, 原下标)，互不相同，所以不稳定的排序也给出稳定的结果
        var keys = new long[n];
        var nodes = list.ToArray();
        for (int i = 0; i < n; i++) keys[i] = ((long)nodes[i].Style.Order << 32) | (uint)i;
        Array.Sort(keys, nodes);
        for (int i = 0; i < n; i++) list[i] = nodes[i];
    }

    readonly Stack<LayoutNode> _hideStack = new();

    /// <summary>
    /// display:none 子树：所有节点的布局结果清零。干净且已隐藏的子树直接跳过。
    /// 用显式栈遍历：隐藏子树不经过 ComputeNode 的栈深度检查，任意深度都不能导致栈溢出。
    /// </summary>
    void HideSubtree(LayoutNode root)
    {
        var stack = _hideStack;
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n._hidden && !n._isDirty) continue;
            n.ZeroLayout();
            n._hidden = true;
            n._isDirty = false;
            n._hasFinal = false;
            var children = n.ChildList;
            for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    /// <summary>完整布局的收尾：写入盒模型与滚动尺寸；若节点是包含块则布局归属它的绝对定位后代。</summary>
    void FinishContainer(LayoutNode node, float w, float h, float baseline, in BoxEdges pad, in BoxEdges bor,
        float extentRight, float extentBottom)
    {
        node._w = w;
        node._h = h;
        node._baseline = baseline;
        node._padding = pad;
        node._border = bor;
        if (node.Style.Position != Position.Static || ReferenceEquals(node, _root))
            LayoutOutOfFlow(node, ref extentRight, ref extentBottom);
        node._scrollW = Math.Max(w - bor.Horizontal, extentRight + pad.Right - bor.Left);
        node._scrollH = Math.Max(h - bor.Vertical, extentBottom + pad.Bottom - bor.Top);
    }

    /// <summary>子节点在父节点边框盒坐标中的右/下延伸（含外边距；溢出可见时含其可滚动溢出）。</summary>
    static void AccumulateExtent(LayoutNode c, ref float right, ref float bottom)
    {
        var cs = c.Style;
        float w = c._w, h = c._h;
        if (cs.OverflowX is Overflow.Visible) w = Math.Max(w, c._border.Left + c._scrollW);
        if (cs.OverflowY is Overflow.Visible) h = Math.Max(h, c._border.Top + c._scrollH);
        right = Math.Max(right, c._x + w + c._margin.Right);
        bottom = Math.Max(bottom, c._y + h + c._margin.Bottom);
    }

    // ======================================================================
    // 像素对齐
    // ======================================================================

    /// <summary>
    /// 按设备缩放把每个盒的边对齐到设备像素（对齐的是绝对坐标下的边而不是尺寸，相邻盒保持无缝）。
    /// 结果写入 <see cref="LayoutNode.SnappedBorderBox"/>；未对齐的结果保持不变，供增量布局继续使用。
    /// </summary>
    public static void RoundToPixels(LayoutNode root, float scale)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        if (!(scale > 0) || float.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        RoundNode(root, 0, 0, 0, 0, scale);
    }

    static void RoundNode(LayoutNode n, float parentAbsX, float parentAbsY, float parentSnapX, float parentSnapY, float scale)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        float absX = parentAbsX + n._x, absY = parentAbsY + n._y;
        float l = SnapEdge(absX, scale), t = SnapEdge(absY, scale);
        float r = SnapEdge(absX + n._w, scale), b = SnapEdge(absY + n._h, scale);
        n._snapX = l - parentSnapX;
        n._snapY = t - parentSnapY;
        n._snapW = r - l;
        n._snapH = b - t;
        var children = n.ChildList;
        for (int i = 0; i < children.Count; i++) RoundNode(children[i], absX, absY, l, t, scale);
    }

    /// <summary>把一条边（逻辑像素坐标）对齐到最近的设备像素边界。</summary>
    public static float SnapEdge(float value, float scale) => (float)Math.Floor(value * scale + 0.5f) / scale;
}
