using System;
using System.Collections.Generic;
using static AwAUI.Layout.LayoutMath;

namespace AwAUI.Layout;

public sealed partial class LayoutEngine
{
    // 项目的对齐（已解析为逻辑 flex 方向：起始 = cross-start）
    const byte ItemStretch = 0, ItemStart = 1, ItemEnd = 2, ItemCenter = 3, ItemBaseline = 4;
    // 分布方式（justify-content / align-content，已解析为逻辑方向）
    const byte DistStart = 0, DistEnd = 1, DistCenter = 2, DistBetween = 3, DistAround = 4, DistEvenly = 5;

    struct FlexItem
    {
        public LayoutNode Node;
        // 外边距（逻辑方向，auto 记为 0）与 auto 标志
        public float MarginMainStart, MarginMainEnd, MarginCrossStart, MarginCrossEnd;
        public bool AutoMainStart, AutoMainEnd, AutoCrossStart, AutoCrossEnd;
        public float PbMain, PbCross;
        // 样式尺寸（边框盒，未钳制，NaN = auto）与 min/max
        public float SizeMain, SizeCross, MinMain, MaxMain, MinCross, MaxCross;
        public bool MinResolved;       // MinMain 已最终确定（自动最小尺寸按需计算）
        public byte Align;
        public float Grow, Shrink;
        public float BaseSize, Hypothetical;
        public bool BaseFromMaxContent;
        public float Target, HypCross, TargetCross;
        public float Violation;
        public bool Frozen;
        public float OffsetMain, OffsetCross;  // 边框盒起点：主轴相对内容盒起点，交叉轴相对所在行起点
        public float Baseline;                 // 交叉起始外边距边到基线的距离（仅基线对齐的项目）
        public float ResMainStart, ResMainEnd, ResCrossStart, ResCrossEnd; // 含 auto 分配后的外边距
    }

    struct FlexLine
    {
        public int Start, Count;
        public float Cross, Offset, MaxBaseline;
    }

    struct FlexCtx
    {
        public bool IsRow, Wrap, MainReversed, CrossReversed;
        public float InnerMain, InnerCross;   // 容器内容盒尺寸，NaN = 尚未确定
        public float PctW, PctH;              // 项目的百分比基准（容器内容盒中「确定」的尺寸）
        public AvailableSpace AvailMain, AvailCross; // 给项目的可用空间
        public float MainGap, CrossGap;
    }

    readonly Stack<FlexItem[]> _itemPool = new();
    readonly Stack<FlexLine[]> _linePool = new();

    FlexItem[] RentItems(int n)
    {
        var a = _itemPool.Count > 0 ? _itemPool.Pop() : new FlexItem[Math.Max(n, 8)];
        return a.Length >= n ? a : new FlexItem[Math.Max(n, a.Length * 2)];
    }

    void ReturnItems(FlexItem[] a, int count)
    {
        Array.Clear(a, 0, count);
        _itemPool.Push(a);
    }

    FlexLine[] RentLines(int n)
    {
        var a = _linePool.Count > 0 ? _linePool.Pop() : new FlexLine[Math.Max(n, 4)];
        return a.Length >= n ? a : new FlexLine[Math.Max(n, a.Length * 2)];
    }

    /// <summary>Flex 容器布局，按 CSS Flexible Box Layout Level 1 §9 的步骤编号组织。</summary>
    LayoutOutput ComputeFlex(LayoutNode node, in LayoutInput input)
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
        if (input.Sizing == SizingMode.Inherent)
        {
            if (!IsDef(w) && sz.MaxWidth <= sz.MinWidth) w = sz.MinWidth;
            if (!IsDef(h) && sz.MaxHeight <= sz.MinHeight) h = sz.MinHeight;
            RatioAsMinHeight(s, ref w, ref h, pbW, pbH, ref sz, input.KnownHeight);
        }

        bool perform = input.IsPerform;
        if (!perform && IsDef(w) && (IsDef(h) || input.WidthOnly) && !input.RequestBaseline) return new LayoutOutput(w, h, float.NaN);

        bool rtl = s.Direction == Direction.Rtl;
        var ctx = new FlexCtx
        {
            IsRow = s.FlexDirection is FlexDirection.Row or FlexDirection.RowReverse,
            Wrap = s.FlexWrap != FlexWrap.NoWrap,
        };
        ctx.MainReversed = ctx.IsRow ? (s.FlexDirection == FlexDirection.RowReverse) != rtl : s.FlexDirection == FlexDirection.ColumnReverse;
        ctx.CrossReversed = (!ctx.IsRow && rtl) != (s.FlexWrap == FlexWrap.WrapReverse);

        var margin = ResolveMargin(s, input.ParentWidth, out _);
        var children = RentList();
        CollectChildren(node, children, perform);
        SortByOrder(children);

        // column 容器的宽度（交叉轴，也是行内轴）未定时先按内容求出：fit-content
        if (!ctx.IsRow && !IsDef(w))
        {
            float content = BlockIntrinsicWidth(children, input.AvailableWidth.Shrink(margin.Horizontal + pbW));
            w = Clamp(content + pbW, sz.MinWidth, sz.MaxWidth);
            if (input.WidthOnly)
            {
                ReturnList(children);
                return new LayoutOutput(w, float.NaN, float.NaN);
            }
            if (input.Sizing == SizingMode.Inherent) RatioAsMinHeight(s, ref w, ref h, pbW, pbH, ref sz, input.KnownHeight);
        }

        float innerW = IsDef(w) ? Math.Max(0, w - pbW) : float.NaN;
        float innerH = IsDef(h) ? Math.Max(0, h - pbH) : float.NaN;
        var availW = IsDef(innerW) ? AvailableSpace.Definite(innerW) : input.AvailableWidth.Shrink(margin.Horizontal + pbW);
        var availH = IsDef(innerH) ? AvailableSpace.Definite(innerH) : input.AvailableHeight.Shrink(margin.Vertical + pbH);
        ctx.PctW = innerW;
        ctx.PctH = innerH;
        ctx.InnerMain = ctx.IsRow ? innerW : innerH;
        ctx.InnerCross = ctx.IsRow ? innerH : innerW;
        ctx.AvailMain = ctx.IsRow ? availW : availH;
        ctx.AvailCross = ctx.IsRow ? availH : availW;
        var availMainOriginal = ctx.AvailMain;

        float pbMain = ctx.IsRow ? pbW : pbH, pbCross = ctx.IsRow ? pbH : pbW;
        float minMainInner = Math.Max(0, (ctx.IsRow ? sz.MinWidth : sz.MinHeight) - pbMain);
        float maxMainInner = Math.Max(0, (ctx.IsRow ? sz.MaxWidth : sz.MaxHeight) - pbMain);
        float minCrossInner = Math.Max(0, (ctx.IsRow ? sz.MinHeight : sz.MinWidth) - pbCross);
        float maxCrossInner = Math.Max(0, (ctx.IsRow ? sz.MaxHeight : sz.MaxWidth) - pbCross);

        var mainGapStyle = ctx.IsRow ? s.ColumnGap : s.RowGap;
        var crossGapStyle = ctx.IsRow ? s.RowGap : s.ColumnGap;
        ctx.MainGap = Math.Max(0, mainGapStyle.ResolveOrZero(ctx.InnerMain));
        ctx.CrossGap = Math.Max(0, crossGapStyle.ResolveOrZero(ctx.InnerCross));

        // §9.1 生成 flex 项目
        var items = RentItems(children.Count);
        int count = 0;
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            if (c.Style.IsOutOfFlow) continue;
            InitItem(ref ctx, s, rtl, ref items[count++], c);
        }

        // §9.2 第 3 步：flex 基准尺寸与假想主尺寸
        for (int i = 0; i < count; i++) DetermineBaseSize(ref ctx, ref items[i]);

        // §9.3 第 5 步：收集 flex 行
        var lines = RentLines(Math.Max(1, count));
        float lineLimit;
        if (!ctx.Wrap) lineLimit = float.PositiveInfinity;
        else if (IsDef(ctx.InnerMain)) lineLimit = ctx.InnerMain;
        else if (ctx.IsRow && ctx.AvailMain.Kind == AvailableSpaceKind.MinContent) lineLimit = 0;
        else if (ctx.IsRow && ctx.AvailMain.IsDefinite) lineLimit = Math.Max(minMainInner, Math.Min(ctx.AvailMain.Value, maxMainInner));
        else lineLimit = Math.Max(minMainInner, maxMainInner);
        int lineCount = CollectLines(ref ctx, items, count, lines, lineLimit);

        // §9.2 第 4 步：容器主尺寸
        bool mainWasIndefinite = !IsDef(ctx.InnerMain);
        if (mainWasIndefinite)
        {
            float content;
            if (ctx.IsRow)
            {
                switch (availMainOriginal.Kind)
                {
                    case AvailableSpaceKind.MinContent:
                        content = IntrinsicMain(ref ctx, items, count, AvailableSpaceKind.MinContent);
                        break;
                    case AvailableSpaceKind.MaxContent:
                        content = IntrinsicMain(ref ctx, items, count, AvailableSpaceKind.MaxContent);
                        break;
                    default:
                        float maxC = IntrinsicMain(ref ctx, items, count, AvailableSpaceKind.MaxContent);
                        float a = availMainOriginal.Value;
                        content = maxC <= a ? maxC
                            : Math.Min(maxC, Math.Max(IntrinsicMain(ref ctx, items, count, AvailableSpaceKind.MinContent), a));
                        break;
                }
            }
            else
            {
                content = 0;
                for (int l = 0; l < lineCount; l++)
                {
                    float sum = ctx.MainGap * Math.Max(0, lines[l].Count - 1);
                    for (int i = lines[l].Start, e = i + lines[l].Count; i < e; i++)
                        sum += items[i].Hypothetical + items[i].MarginMainStart + items[i].MarginMainEnd;
                    content = Math.Max(content, sum);
                }
            }
            ctx.InnerMain = Math.Max(0, Clamp(content, minMainInner, maxMainInner));
            ctx.AvailMain = AvailableSpace.Definite(ctx.InnerMain);
            ctx.MainGap = Math.Max(0, mainGapStyle.ResolveOrZero(ctx.InnerMain));
            if (ctx.IsRow) w = ctx.InnerMain + pbW;
            else h = ctx.InnerMain + pbH;
        }

        if (input.WidthOnly)
        {
            // 只求宽度：row 容器的宽度（主尺寸）在第 4 步已确定（column 容器在前面已返回）
            ReturnItems(items, count);
            _linePool.Push(lines);
            ReturnList(children);
            return new LayoutOutput(w, float.NaN, float.NaN);
        }

        if (mainWasIndefinite && ctx.IsRow)
        {
            // 收缩适应的 row 容器：宽度此时才确定。求固有宽度时项目的百分比（宽度、外边距、内边距、flex-basis）
            // 按不确定处理，真正布局时按最终的内容盒宽度解析（与浏览器一致），所以重新生成项目；
            // 换行容器也按最终宽度重新分行
            ctx.PctW = ctx.InnerMain;
            bool anyPercent = false;
            for (int i = 0; i < count && !anyPercent; i++)
            {
                var c = items[i].Node;
                anyPercent = (c._basisFlags & LayoutNode.UsesParentWidth) != 0 ||
                             c.Style.FlexBasis.Kind is DimensionKind.Percent or DimensionKind.Calc;
            }
            if (anyPercent)
            {
                Array.Clear(items, 0, count);
                count = 0;
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    if (c.Style.IsOutOfFlow) continue;
                    InitItem(ref ctx, s, rtl, ref items[count++], c);
                }
                for (int i = 0; i < count; i++) DetermineBaseSize(ref ctx, ref items[i]);
            }
            if (ctx.Wrap || anyPercent) lineCount = CollectLines(ref ctx, items, count, lines, ctx.Wrap ? ctx.InnerMain : float.PositiveInfinity);
        }

        // §9.7 第 6 步：解析弹性长度
        for (int l = 0; l < lineCount; l++) ResolveFlexibleLengths(ref ctx, items, lines[l]);

        // §9.4 第 7 步：假想交叉尺寸
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            if (IsDef(it.SizeCross)) it.HypCross = Clamp(it.SizeCross, it.MinCross, it.MaxCross);
            else if (it.Align == ItemStretch && !ctx.Wrap && IsDef(ctx.InnerCross) && !it.AutoCrossStart && !it.AutoCrossEnd)
            {
                // 单行且交叉尺寸确定时，被拉伸项目的使用交叉尺寸就是行的交叉尺寸，假想值不会被用到，不必测量
                it.HypCross = ItemMeasureCross(ref ctx, ref it);
            }
            else
            {
                var o = MeasureItem(ref ctx, it.Node, it.Target, float.NaN, SizingMode.Inherent, false);
                it.HypCross = ctx.IsRow ? o.Height : o.Width;
            }
        }

        // 基线（只有 row 方向的基线对齐需要）
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            if (it.Align != ItemBaseline) continue;
            var o = MeasureItem(ref ctx, it.Node, it.Target, it.HypCross, SizingMode.Inherent, true);
            it.Baseline = it.MarginCrossStart + (IsDef(o.Baseline) ? o.Baseline : it.HypCross);
        }

        // 第 8 步：各行交叉尺寸
        for (int l = 0; l < lineCount; l++)
        {
            ref var line = ref lines[l];
            float maxOuter = 0, maxAbove = 0, maxBelow = 0;
            for (int i = line.Start, e = i + line.Count; i < e; i++)
            {
                ref var it = ref items[i];
                float outer = it.HypCross + it.MarginCrossStart + it.MarginCrossEnd;
                if (it.Align == ItemBaseline)
                {
                    maxAbove = Math.Max(maxAbove, it.Baseline);
                    maxBelow = Math.Max(maxBelow, outer - it.Baseline);
                }
                else maxOuter = Math.Max(maxOuter, outer);
            }
            line.MaxBaseline = maxAbove;
            if (!ctx.Wrap && IsDef(ctx.InnerCross)) line.Cross = ctx.InnerCross;
            else
            {
                line.Cross = Math.Max(maxOuter, maxAbove + maxBelow);
                if (!ctx.Wrap) line.Cross = Clamp(line.Cross, minCrossInner, maxCrossInner);
            }
        }

        // 第 9 步：align-content: stretch（多行容器）
        if (ctx.Wrap && s.AlignContent is AlignContent.Stretch or AlignContent.Normal)
        {
            float targetCross = IsDef(ctx.InnerCross) ? ctx.InnerCross : minCrossInner;
            float total = ctx.CrossGap * (lineCount - 1);
            for (int l = 0; l < lineCount; l++) total += lines[l].Cross;
            float free = targetCross - total;
            if (free > 0)
                for (int l = 0; l < lineCount; l++) lines[l].Cross += free / lineCount;
        }

        // 第 11 步：项目的使用交叉尺寸（stretch）
        for (int l = 0; l < lineCount; l++)
        {
            for (int i = lines[l].Start, e = i + lines[l].Count; i < e; i++)
            {
                ref var it = ref items[i];
                if (it.Align == ItemStretch && !IsDef(it.SizeCross) && !it.AutoCrossStart && !it.AutoCrossEnd)
                    it.TargetCross = Clamp(lines[l].Cross - it.MarginCrossStart - it.MarginCrossEnd, it.MinCross, it.MaxCross);
                else
                    it.TargetCross = it.HypCross;
            }
        }

        // 第 12 步：主轴对齐（auto 外边距、justify-content）
        byte justify = MapJustify(s.JustifyContent, ctx.MainReversed != (ctx.IsRow && rtl));
        for (int l = 0; l < lineCount; l++) AlignMain(ref ctx, items, lines[l], justify);

        // 第 13、14 步：交叉轴 auto 外边距与 align-self
        for (int l = 0; l < lineCount; l++) AlignCross(items, lines[l]);

        // 第 15 步：容器交叉尺寸
        float totalLines = ctx.CrossGap * (lineCount - 1);
        for (int l = 0; l < lineCount; l++) totalLines += lines[l].Cross;
        if (!IsDef(ctx.InnerCross))
        {
            ctx.InnerCross = Math.Max(0, Clamp(totalLines, minCrossInner, maxCrossInner));
            if (ctx.IsRow) h = ctx.InnerCross + pbH;
            else w = ctx.InnerCross + pbW;
        }

        // 第 16 步：align-content
        if (ctx.Wrap)
        {
            byte dist = MapAlignContent(s.AlignContent, ctx.CrossReversed != (!ctx.IsRow && rtl));
            Distribute(dist, ctx.InnerCross - totalLines, lineCount, out float lead, out float between);
            float pos = lead;
            for (int l = 0; l < lineCount; l++)
            {
                lines[l].Offset = pos;
                pos += lines[l].Cross + ctx.CrossGap + between;
            }
        }
        else lines[0].Offset = 0;

        float baseline = float.NaN;
        // 首行有基线对齐的项目时由它们决定容器基线
        bool firstLineHasBaseline = false;
        for (int i = lines[0].Start, e = i + lines[0].Count; i < e; i++)
            if (items[i].Align == ItemBaseline) { firstLineHasBaseline = true; break; }

        if (perform)
        {
            float extentR = 0, extentB = 0;
            for (int l = 0; l < lineCount; l++)
            {
                for (int i = lines[l].Start, e = i + lines[l].Count; i < e; i++)
                {
                    ref var it = ref items[i];
                    ItemPosition(ref ctx, in it, in lines[l], w, h, in pad, in bor, out float x, out float y);
                    var ci = new LayoutInput
                    {
                        KnownWidth = ctx.IsRow ? it.Target : it.TargetCross,
                        KnownHeight = ctx.IsRow ? it.TargetCross : it.Target,
                        ParentWidth = ctx.PctW,
                        ParentHeight = ctx.PctH,
                        AvailableWidth = ctx.IsRow ? ctx.AvailMain : ctx.AvailCross,
                        AvailableHeight = ctx.IsRow ? ctx.AvailCross : ctx.AvailMain,
                        Mode = RunMode.PerformLayout,
                        Sizing = SizingMode.Inherent,
                    };
                    var o = ComputeNode(it.Node, ci);
                    var c = it.Node;
                    RelativeOffset(c.Style, ctx.PctW, ctx.PctH, rtl, out float dx, out float dy);
                    c._x = x + dx;
                    c._y = y + dy;
                    c._margin = PhysicalMargins(ref ctx, in it);
                    AccumulateExtent(c, ref extentR, ref extentB);
                    if (l == 0 && !IsDef(baseline))
                        baseline = ContainerBaselineCandidate(firstLineHasBaseline, in it, y, o);
                }
            }

            // 绝对定位子节点的静态位置：视作容器中唯一的 flex 项目（§4.1）
            RecordFlexTracks(node, items, count, ctx.IsRow, bor.Left + pad.Left, bor.Top + pad.Top);
            for (int i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (!c.Style.IsOutOfFlow) continue;
                SetFlexStaticPosition(ref ctx, s, rtl, c, w, h, in pad, in bor);
            }

            FinishContainer(node, w, h, baseline, pad, bor, extentR, extentB);
        }
        else if (input.RequestBaseline && count > 0)
        {
            for (int i = lines[0].Start, e = i + lines[0].Count; i < e && !IsDef(baseline); i++)
            {
                ref var it = ref items[i];
                if (firstLineHasBaseline && it.Align != ItemBaseline) continue;
                ItemPosition(ref ctx, in it, in lines[0], w, h, in pad, in bor, out _, out float y);
                var o = firstLineHasBaseline
                    ? default
                    : MeasureItem(ref ctx, it.Node, it.Target, it.TargetCross, SizingMode.Inherent, true);
                baseline = ContainerBaselineCandidate(firstLineHasBaseline, in it, y, o);
            }
        }

        ReturnItems(items, count);
        _linePool.Push(lines);
        ReturnList(children);
        return new LayoutOutput(w, h, baseline);
    }

    /// <summary>
    /// 记录 flex 项目的主轴尺寸与起点（相对容器内容盒），供 DevTools 的布局网格叠加绘制。
    /// flex 只有主轴上的「轨道」（每个项目一条）；交叉轴没有轨道数据。项目按布局顺序记录。
    /// 复用上次的数组（项目数不变时零分配），避免完整重排产生可观察的额外分配。
    /// </summary>
    static void RecordFlexTracks(LayoutNode node, FlexItem[] items, int count, bool isRow, float contentLeft, float contentTop)
    {
        if (count == 0)
        {
            if (isRow) { node._usedTracksX = null; node._usedTrackPositionsX = null; }
            else { node._usedTracksY = null; node._usedTrackPositionsY = null; }
            return;
        }
        float[] sizes, positions;
        if (isRow)
        {
            sizes = node._usedTracksX is { } sx && sx.Length == count ? sx : new float[count];
            positions = node._usedTrackPositionsX is { } px && px.Length == count ? px : new float[count];
            // 主轴方向变化时清掉旧的交叉轴数据
            node._usedTracksY = null;
            node._usedTrackPositionsY = null;
            node._usedTracksX = sizes;
            node._usedTrackPositionsX = positions;
        }
        else
        {
            sizes = node._usedTracksY is { } sy && sy.Length == count ? sy : new float[count];
            positions = node._usedTrackPositionsY is { } py && py.Length == count ? py : new float[count];
            node._usedTracksX = null;
            node._usedTrackPositionsX = null;
            node._usedTracksY = sizes;
            node._usedTrackPositionsY = positions;
        }
        for (int i = 0; i < count; i++)
        {
            var c = items[i].Node;
            if (isRow)
            {
                sizes[i] = Math.Max(0, c._w);
                positions[i] = c._x - contentLeft;
            }
            else
            {
                sizes[i] = Math.Max(0, c._h);
                positions[i] = c._y - contentTop;
            }
        }
    }

    void InitItem(ref FlexCtx ctx, LayoutStyle container, bool rtl, ref FlexItem it, LayoutNode c)
    {
        var cs = c.Style;
        it.Node = c;
        var m = ResolveMargin(cs, ctx.PctW, out int autos);
        var pad = ResolvePadding(cs, ctx.PctW);
        var bor = ResolveBorder(cs);
        float pbW = pad.Horizontal + bor.Horizontal, pbH = pad.Vertical + bor.Vertical;
        ResolveSizes(c, ctx.PctW, ctx.PctH, pbW, pbH, out var sz);

        bool row = ctx.IsRow;
        // 逻辑起止边对应的物理边
        float mL = m.Left, mR = m.Right, mT = m.Top, mB = m.Bottom;
        bool aL = (autos & AutoLeft) != 0, aR = (autos & AutoRight) != 0, aT = (autos & AutoTop) != 0, aB = (autos & AutoBottom) != 0;
        if (row)
        {
            it.MarginMainStart = ctx.MainReversed ? mR : mL; it.AutoMainStart = ctx.MainReversed ? aR : aL;
            it.MarginMainEnd = ctx.MainReversed ? mL : mR; it.AutoMainEnd = ctx.MainReversed ? aL : aR;
            it.MarginCrossStart = ctx.CrossReversed ? mB : mT; it.AutoCrossStart = ctx.CrossReversed ? aB : aT;
            it.MarginCrossEnd = ctx.CrossReversed ? mT : mB; it.AutoCrossEnd = ctx.CrossReversed ? aT : aB;
        }
        else
        {
            it.MarginMainStart = ctx.MainReversed ? mB : mT; it.AutoMainStart = ctx.MainReversed ? aB : aT;
            it.MarginMainEnd = ctx.MainReversed ? mT : mB; it.AutoMainEnd = ctx.MainReversed ? aT : aB;
            it.MarginCrossStart = ctx.CrossReversed ? mR : mL; it.AutoCrossStart = ctx.CrossReversed ? aR : aL;
            it.MarginCrossEnd = ctx.CrossReversed ? mL : mR; it.AutoCrossEnd = ctx.CrossReversed ? aL : aR;
        }

        it.PbMain = row ? pbW : pbH;
        it.PbCross = row ? pbH : pbW;
        it.SizeMain = row ? sz.Width : sz.Height;
        it.SizeCross = row ? sz.Height : sz.Width;
        it.MaxMain = row ? sz.MaxWidth : sz.MaxHeight;
        it.MinCross = row ? sz.MinHeight : sz.MinWidth;
        it.MaxCross = row ? sz.MaxHeight : sz.MaxWidth;
        it.MinMain = row ? sz.MinWidth : sz.MinHeight;
        // min-width/height: auto 且不是滚动容器 → 基于内容的自动最小尺寸（§4.5），按需计算
        bool minAuto = (row ? cs.MinWidth : cs.MinHeight).IsAuto && !cs.IsScrollContainer;
        it.MinResolved = !minAuto;

        it.Grow = cs.FlexGrow > 0 ? Math.Min(cs.FlexGrow, MaxFactor) : 0;
        it.Shrink = cs.FlexShrink > 0 ? Math.Min(cs.FlexShrink, MaxFactor) : 0;

        var align = cs.AlignSelf;
        AlignItems resolved = align switch
        {
            AlignSelf.Auto => container.AlignItems,
            AlignSelf.Normal => AlignItems.Normal,
            AlignSelf.Stretch => AlignItems.Stretch,
            AlignSelf.FlexStart => AlignItems.FlexStart,
            AlignSelf.FlexEnd => AlignItems.FlexEnd,
            AlignSelf.Start => AlignItems.Start,
            AlignSelf.End => AlignItems.End,
            AlignSelf.Center => AlignItems.Center,
            _ => AlignItems.Baseline,
        };
        // start/end 按书写方向：交叉轴的书写方向只在 column + rtl 时反向
        bool startIsFlexEnd = ctx.CrossReversed != (!row && rtl);
        it.Align = resolved switch
        {
            AlignItems.Normal or AlignItems.Stretch => ItemStretch,
            AlignItems.FlexStart => ItemStart,
            AlignItems.FlexEnd => ItemEnd,
            AlignItems.Start => startIsFlexEnd ? ItemEnd : ItemStart,
            AlignItems.End => startIsFlexEnd ? ItemStart : ItemEnd,
            AlignItems.Center => ItemCenter,
            // 基线对齐只用于 row 方向且交叉轴未反转；其余回退为 flex-start
            _ => row && !ctx.CrossReversed && !it.AutoCrossStart && !it.AutoCrossEnd ? ItemBaseline : ItemStart,
        };
    }

    /// <summary>§9.2 第 3 步。</summary>
    void DetermineBaseSize(ref FlexCtx ctx, ref FlexItem it)
    {
        var cs = it.Node.Style;
        var basis = cs.FlexBasis;
        float b = float.NaN;
        bool definiteBasis = false;
        it.BaseFromMaxContent = false;

        if (basis.Kind is DimensionKind.Length or DimensionKind.Percent or DimensionKind.Calc)
        {
            float v = basis.Resolve(ctx.InnerMain);
            if (IsDef(v) && !float.IsInfinity(v))
            {
                b = Math.Max(0, v) + (cs.BoxSizing == BoxSizing.ContentBox ? it.PbMain : 0);
                definiteBasis = true;
            }
        }
        else if (basis.Kind == DimensionKind.Auto && IsDef(it.SizeMain))
        {
            b = it.SizeMain;
        }

        if (!IsDef(b))
        {
            if (cs.AspectRatio is float ratio && ratio > 0 && !float.IsInfinity(ratio) && IsDef(it.SizeCross))
            {
                // A：有宽高比且交叉尺寸确定，按比例换算
                float adjMain = cs.BoxSizing == BoxSizing.ContentBox ? it.PbMain : 0;
                float adjCross = cs.BoxSizing == BoxSizing.ContentBox ? it.PbCross : 0;
                float cross = Clamp(it.SizeCross, it.MinCross, it.MaxCross) - adjCross;
                b = Lim((ctx.IsRow ? cross * ratio : cross / ratio) + adjMain);
                definiteBasis = true;
            }
            else
            {
                // E：按内容（content 视作 max-content；容器以 min-content 求尺寸时用 min-content）
                bool min = basis.Kind == DimensionKind.MinContent || ctx.AvailMain.Kind == AvailableSpaceKind.MinContent;
                b = MeasureContentMain(ref ctx, ref it, min ? AvailableSpaceKind.MinContent : AvailableSpaceKind.MaxContent);
                it.BaseFromMaxContent = !min;
            }
        }

        it.BaseSize = Math.Max(b, it.PbMain);
        // 内容决定的基准尺寸不小于基于内容的最小尺寸，只有确定的 flex-basis 才需要立即求自动最小尺寸
        float min2 = it.MinResolved ? it.MinMain : definiteBasis ? AutoMinMain(ref ctx, ref it) : it.PbMain;
        it.Hypothetical = Clamp(it.BaseSize, min2, it.MaxMain);
    }

    /// <summary>项目的内容主尺寸（忽略项目自身的尺寸属性）。交叉尺寸确定或会被拉伸时作为已知值传入。</summary>
    float MeasureContentMain(ref FlexCtx ctx, ref FlexItem it, AvailableSpaceKind kind)
    {
        float kc = ItemMeasureCross(ref ctx, ref it);
        if (!ctx.IsRow && !IsDef(kc))
        {
            // column 容器：交叉尺寸（宽度）为 auto 且不会被拉伸时，§9.2 3E 要求以 fit-content 作为交叉尺寸来求主尺寸（高度）。
            // fit-content 受项目自身 min/max-width 约束，所以按 Inherent 只求宽度，而不能在 Content 模式里顺带求
            // （Content 模式忽略项目自身的尺寸属性，max-width 窄于可用宽度时会按过宽的宽度断行，高度偏小）
            kc = MeasureNode(it.Node, float.NaN, float.NaN, ctx.PctW, ctx.PctH, ctx.AvailCross, AvailableSpace.MaxContent,
                SizingMode.Inherent, widthOnly: true).Width;
        }
        var am = kind == AvailableSpaceKind.MinContent ? AvailableSpace.MinContent : AvailableSpace.MaxContent;
        var input = new LayoutInput
        {
            KnownWidth = ctx.IsRow ? float.NaN : kc,
            KnownHeight = ctx.IsRow ? kc : float.NaN,
            ParentWidth = ctx.PctW,
            ParentHeight = ctx.PctH,
            AvailableWidth = ctx.IsRow ? am : ctx.AvailCross,
            AvailableHeight = ctx.IsRow ? ctx.AvailCross : am,
            Mode = RunMode.ComputeSize,
            Sizing = SizingMode.Content,
            WidthOnly = ctx.IsRow,
        };
        var o = ComputeNode(it.Node, input);
        return ctx.IsRow ? o.Width : o.Height;
    }

    static float ItemMeasureCross(ref FlexCtx ctx, ref FlexItem it)
    {
        if (IsDef(it.SizeCross)) return Clamp(it.SizeCross, it.MinCross, it.MaxCross);
        if (it.Align == ItemStretch && !it.AutoCrossStart && !it.AutoCrossEnd && !ctx.Wrap && IsDef(ctx.InnerCross))
            return Clamp(ctx.InnerCross - it.MarginCrossStart - it.MarginCrossEnd, it.MinCross, it.MaxCross);
        return float.NaN;
    }

    /// <summary>§4.5 自动最小尺寸：min(内容尺寸建议, 指定尺寸建议)，再受最大尺寸限制。</summary>
    float AutoMinMain(ref FlexCtx ctx, ref FlexItem it)
    {
        if (it.MinResolved) return it.MinMain;
        float v = MeasureContentMain(ref ctx, ref it, AvailableSpaceKind.MinContent);
        if (IsDef(it.SizeMain)) v = Math.Min(v, it.SizeMain);
        v = Math.Min(v, it.MaxMain);
        it.MinMain = Math.Max(v, it.PbMain);
        it.MinResolved = true;
        return it.MinMain;
    }

    LayoutOutput MeasureItem(ref FlexCtx ctx, LayoutNode n, float knownMain, float knownCross, SizingMode sizing, bool baseline)
    {
        var input = new LayoutInput
        {
            KnownWidth = ctx.IsRow ? knownMain : knownCross,
            KnownHeight = ctx.IsRow ? knownCross : knownMain,
            ParentWidth = ctx.PctW,
            ParentHeight = ctx.PctH,
            AvailableWidth = ctx.IsRow ? ctx.AvailMain : ctx.AvailCross,
            AvailableHeight = ctx.IsRow ? ctx.AvailCross : ctx.AvailMain,
            Mode = RunMode.ComputeSize,
            Sizing = sizing,
            RequestBaseline = baseline,
        };
        return ComputeNode(n, input);
    }

    static int CollectLines(ref FlexCtx ctx, FlexItem[] items, int count, FlexLine[] lines, float limit)
    {
        int lc = 0, start = 0;
        float acc = 0;
        for (int i = 0; i < count; i++)
        {
            float outer = items[i].Hypothetical + items[i].MarginMainStart + items[i].MarginMainEnd;
            if (i > start && acc + ctx.MainGap + outer > limit + 0.001f)
            {
                lines[lc++] = new FlexLine { Start = start, Count = i - start };
                start = i;
                acc = outer;
            }
            else acc += (i > start ? ctx.MainGap : 0) + outer;
        }
        lines[lc++] = new FlexLine { Start = start, Count = count - start };
        return lc;
    }

    /// <summary>
    /// row 容器内容的固有主尺寸。max-content：各项目 max-content 贡献之和；
    /// min-content：单行为各项目 min-content 贡献之和，多行为最大值。（与 Chromium 一致，不考虑 flex-basis。）
    /// </summary>
    float IntrinsicMain(ref FlexCtx ctx, FlexItem[] items, int count, AvailableSpaceKind kind)
    {
        float sum = 0, max = 0;
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            float size;
            if (IsDef(it.SizeMain)) size = it.SizeMain;
            else if (kind == AvailableSpaceKind.MaxContent && it.BaseFromMaxContent) size = it.BaseSize;
            else size = MeasureContentMain(ref ctx, ref it, kind);
            float min = it.MinResolved ? it.MinMain : it.PbMain;
            float contribution = Clamp(size, min, it.MaxMain) + it.MarginMainStart + it.MarginMainEnd;
            sum += contribution;
            max = Math.Max(max, contribution);
        }
        float gaps = ctx.MainGap * Math.Max(0, count - 1);
        if (kind == AvailableSpaceKind.MinContent && ctx.Wrap) return max;
        return sum + gaps;
    }

    /// <summary>§9.7 解析弹性长度（含 min/max 违例的冻结循环）。</summary>
    void ResolveFlexibleLengths(ref FlexCtx ctx, FlexItem[] items, FlexLine line)
    {
        int s = line.Start, e = s + line.Count;
        if (line.Count == 0) return;
        float innerMain = ctx.InnerMain;
        float gaps = ctx.MainGap * (line.Count - 1);

        float sumHyp = gaps;
        for (int i = s; i < e; i++) sumHyp += items[i].Hypothetical + items[i].MarginMainStart + items[i].MarginMainEnd;
        bool grow = sumHyp < innerMain;

        // 1. 冻结不可伸缩的项目
        for (int i = s; i < e; i++)
        {
            ref var it = ref items[i];
            it.Target = it.Hypothetical;
            float factor = grow ? it.Grow : it.Shrink;
            it.Frozen = factor == 0 || (grow && it.BaseSize > it.Hypothetical) || (!grow && it.BaseSize < it.Hypothetical);
        }

        // 2. 初始自由空间
        float initialFree = innerMain - gaps;
        for (int i = s; i < e; i++)
        {
            ref var it = ref items[i];
            initialFree -= (it.Frozen ? it.Target : it.BaseSize) + it.MarginMainStart + it.MarginMainEnd;
        }

        // 3. 循环：每轮至少冻结一个项目，最多 Count 轮
        for (int iter = 0; iter <= line.Count; iter++)
        {
            float used = gaps, sumFactors = 0;
            bool anyUnfrozen = false;
            for (int i = s; i < e; i++)
            {
                ref var it = ref items[i];
                used += (it.Frozen ? it.Target : it.BaseSize) + it.MarginMainStart + it.MarginMainEnd;
                if (!it.Frozen)
                {
                    anyUnfrozen = true;
                    sumFactors += grow ? it.Grow : it.Shrink;
                }
            }
            if (!anyUnfrozen) break;

            float free = innerMain - used;
            if (sumFactors < 1)
            {
                float scaled = initialFree * sumFactors;
                if (Math.Abs(scaled) < Math.Abs(free)) free = scaled;
            }

            if (grow)
            {
                for (int i = s; i < e; i++)
                {
                    ref var it = ref items[i];
                    if (it.Frozen) continue;
                    // 先求比例再乘自由空间，避免大因子下的中间量溢出
                    it.Target = it.BaseSize + (sumFactors > 0 ? free * (it.Grow / sumFactors) : 0);
                }
            }
            else
            {
                float sumScaled = 0;
                for (int i = s; i < e; i++)
                {
                    ref var it = ref items[i];
                    if (!it.Frozen) sumScaled += it.Shrink * (it.BaseSize - it.PbMain);
                }
                for (int i = s; i < e; i++)
                {
                    ref var it = ref items[i];
                    if (it.Frozen) continue;
                    it.Target = sumScaled > 0 ? it.BaseSize + free * (it.Shrink * (it.BaseSize - it.PbMain) / sumScaled) : it.BaseSize;
                }
            }

            // 修正 min/max 违例
            float total = 0;
            for (int i = s; i < e; i++)
            {
                ref var it = ref items[i];
                if (it.Frozen) continue;
                float min = it.MinResolved ? it.MinMain : it.Target < it.BaseSize ? AutoMinMain(ref ctx, ref it) : it.PbMain;
                float clamped = Clamp(it.Target, min, it.MaxMain);
                it.Violation = clamped - it.Target;
                it.Target = clamped;
                total += it.Violation;
            }

            bool done = Math.Abs(total) < 0.0001f;
            for (int i = s; i < e; i++)
            {
                ref var it = ref items[i];
                if (it.Frozen) continue;
                if (done || (total > 0 && it.Violation > 0) || (total < 0 && it.Violation < 0)) it.Frozen = true;
            }
            if (done) break;
        }
    }

    static byte MapJustify(JustifyContent j, bool startIsFlexEnd) => j switch
    {
        JustifyContent.FlexEnd => DistEnd,
        JustifyContent.Start => startIsFlexEnd ? DistEnd : DistStart,
        JustifyContent.End => startIsFlexEnd ? DistStart : DistEnd,
        JustifyContent.Center => DistCenter,
        JustifyContent.SpaceBetween => DistBetween,
        JustifyContent.SpaceAround => DistAround,
        JustifyContent.SpaceEvenly => DistEvenly,
        _ => DistStart,
    };

    static byte MapAlignContent(AlignContent a, bool startIsFlexEnd) => a switch
    {
        AlignContent.FlexEnd => DistEnd,
        AlignContent.Start => startIsFlexEnd ? DistEnd : DistStart,
        AlignContent.End => startIsFlexEnd ? DistStart : DistEnd,
        AlignContent.Center => DistCenter,
        AlignContent.SpaceBetween => DistBetween,
        AlignContent.SpaceAround => DistAround,
        AlignContent.SpaceEvenly => DistEvenly,
        _ => DistStart,
    };

    /// <summary>按分布方式求首个元素前的偏移与相邻元素间的额外间距。自由空间为负时按规范回退。</summary>
    static void Distribute(byte dist, float free, int n, out float lead, out float between)
    {
        lead = between = 0;
        switch (dist)
        {
            case DistEnd:
                lead = free;
                break;
            case DistCenter:
                lead = free / 2;
                break;
            case DistBetween:
                if (free > 0 && n > 1) between = free / (n - 1);
                break;
            case DistAround:
                if (free > 0 && n > 0) { between = free / n; lead = between / 2; }
                else lead = free / 2;
                break;
            case DistEvenly:
                if (free > 0) { between = free / (n + 1); lead = between; }
                else lead = free / 2;
                break;
        }
    }

    static void AlignMain(ref FlexCtx ctx, FlexItem[] items, FlexLine line, byte justify)
    {
        int s = line.Start, e = s + line.Count;
        float used = ctx.MainGap * Math.Max(0, line.Count - 1);
        int autoCount = 0;
        for (int i = s; i < e; i++)
        {
            ref var it = ref items[i];
            used += it.Target + it.MarginMainStart + it.MarginMainEnd;
            if (it.AutoMainStart) autoCount++;
            if (it.AutoMainEnd) autoCount++;
        }
        float free = ctx.InnerMain - used;
        float autoSize = 0;
        if (free > 0 && autoCount > 0)
        {
            autoSize = free / autoCount;
            free = 0;
        }
        Distribute(justify, free, line.Count, out float lead, out float between);
        float pos = lead;
        for (int i = s; i < e; i++)
        {
            ref var it = ref items[i];
            it.ResMainStart = it.MarginMainStart + (it.AutoMainStart ? autoSize : 0);
            it.ResMainEnd = it.MarginMainEnd + (it.AutoMainEnd ? autoSize : 0);
            it.OffsetMain = pos + it.ResMainStart;
            pos += it.ResMainStart + it.Target + it.ResMainEnd + ctx.MainGap + between;
        }
    }

    static void AlignCross(FlexItem[] items, FlexLine line)
    {
        for (int i = line.Start, e = i + line.Count; i < e; i++)
        {
            ref var it = ref items[i];
            float mcs = it.MarginCrossStart, mce = it.MarginCrossEnd;
            float free = line.Cross - (it.TargetCross + mcs + mce);
            float offset;
            if (it.AutoCrossStart || it.AutoCrossEnd)
            {
                if (free > 0)
                {
                    if (it.AutoCrossStart && it.AutoCrossEnd) { mcs += free / 2; mce += free / 2; }
                    else if (it.AutoCrossStart) mcs += free;
                    else mce += free;
                }
                offset = mcs;
            }
            else
            {
                float off = it.Align switch
                {
                    ItemEnd => free,
                    ItemCenter => free / 2,
                    ItemBaseline => line.MaxBaseline - it.Baseline,
                    _ => 0,
                };
                offset = off + mcs;
            }
            it.ResCrossStart = mcs;
            it.ResCrossEnd = mce;
            it.OffsetCross = offset;
        }
    }

    /// <summary>项目边框盒在容器边框盒坐标中的物理位置。</summary>
    static void ItemPosition(ref FlexCtx ctx, in FlexItem it, in FlexLine line, float w, float h,
        in BoxEdges pad, in BoxEdges bor, out float x, out float y)
    {
        float mainStart, mainEnd, crossStart, crossEnd, outerMain, outerCross;
        if (ctx.IsRow)
        {
            mainStart = bor.Left + pad.Left; mainEnd = bor.Right + pad.Right; outerMain = w;
            crossStart = bor.Top + pad.Top; crossEnd = bor.Bottom + pad.Bottom; outerCross = h;
        }
        else
        {
            mainStart = bor.Top + pad.Top; mainEnd = bor.Bottom + pad.Bottom; outerMain = h;
            crossStart = bor.Left + pad.Left; crossEnd = bor.Right + pad.Right; outerCross = w;
        }
        float main = ctx.MainReversed ? outerMain - mainEnd - it.OffsetMain - it.Target : mainStart + it.OffsetMain;
        float crossLogical = line.Offset + it.OffsetCross;
        float cross = ctx.CrossReversed ? outerCross - crossEnd - crossLogical - it.TargetCross : crossStart + crossLogical;
        x = ctx.IsRow ? main : cross;
        y = ctx.IsRow ? cross : main;
    }

    static BoxEdges PhysicalMargins(ref FlexCtx ctx, in FlexItem it)
    {
        float ms = it.ResMainStart, me = it.ResMainEnd, cs = it.ResCrossStart, ce = it.ResCrossEnd;
        if (ctx.IsRow)
        {
            float l = ctx.MainReversed ? me : ms, r = ctx.MainReversed ? ms : me;
            float t = ctx.CrossReversed ? ce : cs, b = ctx.CrossReversed ? cs : ce;
            return new BoxEdges(l, t, r, b);
        }
        else
        {
            float t = ctx.MainReversed ? me : ms, b = ctx.MainReversed ? ms : me;
            float l = ctx.CrossReversed ? ce : cs, r = ctx.CrossReversed ? cs : ce;
            return new BoxEdges(l, t, r, b);
        }
    }

    /// <summary>
    /// 容器的第一条基线：首行有基线对齐的项目时取共享基线，否则取首行第一个项目的基线（没有则以其边框盒底边合成）。
    /// 由首行的项目依次调用，返回 NaN 表示该项目不决定基线。<paramref name="o"/> 只在首行没有基线对齐项目时使用。
    /// </summary>
    static float ContainerBaselineCandidate(bool firstLineHasBaseline, in FlexItem it, float itemY, in LayoutOutput o)
    {
        if (firstLineHasBaseline)
            return it.Align == ItemBaseline ? itemY + (it.Baseline - it.MarginCrossStart) : float.NaN;
        return itemY + (IsDef(o.Baseline) ? o.Baseline : o.Height);
    }

    void SetFlexStaticPosition(ref FlexCtx ctx, LayoutStyle container, bool rtl, LayoutNode c, float w, float h,
        in BoxEdges pad, in BoxEdges bor)
    {
        c._staticX = bor.Left + pad.Left;
        c._staticY = bor.Top + pad.Top;
        c._staticW = Math.Max(0, w - bor.Horizontal - pad.Horizontal);
        c._staticH = Math.Max(0, h - bor.Vertical - pad.Vertical);

        // 主轴：justify-content（space-between → 起始，space-around/evenly → 居中）
        byte dist = MapJustify(container.JustifyContent, ctx.MainReversed != (ctx.IsRow && rtl));
        byte mainLogical = dist switch
        {
            DistEnd => AlignEnd,
            DistCenter or DistAround or DistEvenly => AlignCenter,
            _ => AlignStart,
        };
        byte mainPhys = ToPhysical(mainLogical, ctx.MainReversed);

        // 交叉轴：align-self（stretch/baseline 按 flex-start）
        var cs = c.Style;
        AlignItems a = cs.AlignSelf switch
        {
            AlignSelf.Auto => container.AlignItems,
            AlignSelf.FlexEnd => AlignItems.FlexEnd,
            AlignSelf.Center => AlignItems.Center,
            AlignSelf.Start => AlignItems.Start,
            AlignSelf.End => AlignItems.End,
            _ => AlignItems.FlexStart,
        };
        bool startIsFlexEnd = ctx.CrossReversed != (!ctx.IsRow && rtl);
        byte crossLogical = a switch
        {
            AlignItems.FlexEnd => AlignEnd,
            AlignItems.Center => AlignCenter,
            AlignItems.Start => startIsFlexEnd ? AlignEnd : AlignStart,
            AlignItems.End => startIsFlexEnd ? AlignStart : AlignEnd,
            _ => AlignStart,
        };
        byte crossPhys = ToPhysical(crossLogical, ctx.CrossReversed);

        c._staticAlignX = ctx.IsRow ? mainPhys : crossPhys;
        c._staticAlignY = ctx.IsRow ? crossPhys : mainPhys;
    }

    static byte ToPhysical(byte logical, bool reversed) =>
        !reversed || logical == AlignCenter ? logical : logical == AlignStart ? AlignEnd : AlignStart;
}
