using System;
using System.Collections.Generic;
using static AwAUI.Layout.LayoutMath;

namespace AwAUI.Layout;

/// <summary>
/// 网格布局（CSS Grid Layout Module Level 1）：显式 / 隐式网格、按线与按区域放置、自动放置（row / column、dense）、
/// 轨道尺寸算法（§11：固有贡献、跨越多轨道的项目、fr 在确定与不确定空间下的求解、拉伸 auto 轨道）、间距、
/// justify/align-items/self/content、百分比轨道、min/max 尺寸、绝对定位子节点以网格区域为包含块。
/// <para>
/// 未实现：子网格、瀑布流、基线对齐（<c>baseline</c> 按 start）、按行尺寸重算列（§11.1 第 3、4 步，只影响依赖行尺寸的列贡献）。
/// </para>
/// <para>
/// 防御不可信 CSS：网格线编号钳制到 ±<see cref="GridLine.MaxLine"/>，显式网格至多 <c>MaxLine - 1</c> 条轨道，
/// 自动放置不越过 <c>MaxLine</c>；占用表按「主轴带」存储，内存与跨度数值无关（O(项目数²) 上界）；
/// 记录的线名总数至多 <see cref="MaxRecordedLineNames"/>。
/// </para>
/// </summary>
public sealed partial class LayoutEngine
{
    /// <summary>展开轨道列表时记录的线名条目上限（超出部分的线名被忽略）。</summary>
    internal const int MaxRecordedLineNames = 1 << 17;

    // 轨道尺寸函数种类
    const byte TkFixed = 0, TkAuto = 1, TkMinC = 2, TkMaxC = 3, TkFlex = 4, TkFit = 5;
    // 轴的尺寸约束
    const byte CDef = 0, CMin = 1, CMax = 2;

    struct GTrack
    {
        public GridTrackSize Fn;
        public bool Collapsed, AutoFit;
        public byte MinK, MaxK;
        public float MinV, MaxV, Flex;
        public float Base, Limit, Planned, Inc;
        public bool InfGrow, Touched, Inflexible;
        public float Pos;
        public float End => Pos + Base;
    }

    struct GItem
    {
        public LayoutNode Node;
        public int R0, R1, C0, C1;            // 轨道下标 [起, 止)（放置阶段为显式网格坐标）
        public bool RowDef, ColDef;
        public int RowSpan, ColSpan;
        public byte JAlign, AAlign;           // ItemStretch / ItemStart / ItemEnd / ItemCenter（逻辑方向）
        public float MinC, MaxC, Minimum;      // 当前轴的贡献（NaN = 未求）
        public bool SpansAutoMin, CrossesFlex;
        public float FixedMax;                 // 所跨轨道的最大尺寸函数都固定时：其和加中间的间距（§6.6 钳制自动最小尺寸），否则 +∞
        public float AreaW, WidthFor, W;       // 行轴：所在区域的宽度与项目在其中的宽度
        public float MT, MB;                   // 行轴贡献用的上下外边距（相对区域宽度）
    }

    /// <summary>一个轴的显式网格（展开后的轨道尺寸与线名）。</summary>
    sealed class GridAxis
    {
        public readonly List<GridTrackSize> Sizes = new();
        public readonly List<bool> AutoFit = new();
        public int Explicit;
        public Dictionary<string, List<int>>? Names;
        public int NameCount;
        public GridAreas? Areas;
        public bool IsRow;
        public GridTrackSize[] Auto = Array.Empty<GridTrackSize>();
        public int Min, Max;    // 放置后的隐式网格范围（显式坐标中的线下标）

        public void Reset()
        {
            Sizes.Clear();
            AutoFit.Clear();
            Explicit = 0;
            Names?.Clear();
            NameCount = 0;
            Areas = null;
        }

        public void AddName(string name, int line)
        {
            if (NameCount >= MaxRecordedLineNames) return;
            Names ??= new Dictionary<string, List<int>>(StringComparer.Ordinal);
            if (!Names.TryGetValue(name, out var list)) Names[name] = list = new List<int>();
            if (list.Count > 0 && list[list.Count - 1] == line) return;
            list.Add(line);
            NameCount++;
        }

        /// <summary>名为 <paramref name="name"/> 的显式网格线（升序）；包括区域隐式定义的 <c>x-start / x-end</c>。</summary>
        public List<int>? Lines(string name, List<int> scratch)
        {
            List<int>? fromTemplate = null;
            Names?.TryGetValue(name, out fromTemplate);
            int areaLine = -1;
            if (Areas is not null)
            {
                bool start = name.EndsWith("-start", StringComparison.Ordinal);
                bool end = !start && name.EndsWith("-end", StringComparison.Ordinal);
                if (start || end)
                {
                    var area = name.Substring(0, name.Length - (start ? 6 : 4));
                    if (Areas.TryFind(area, out var a))
                        areaLine = IsRow ? (start ? a.RowStart : a.RowEnd) : (start ? a.ColumnStart : a.ColumnEnd);
                }
            }
            if (areaLine < 0) return fromTemplate;
            scratch.Clear();
            if (fromTemplate is not null) scratch.AddRange(fromTemplate);
            int i = scratch.BinarySearch(areaLine);
            if (i < 0) scratch.Insert(~i, areaLine);
            return scratch;
        }

        public GridTrackSize SizeOf(int line)
        {
            if (line >= 0 && line < Sizes.Count) return Sizes[line];
            int len = Auto.Length;
            if (len == 0) return GridTrackSize.Auto;
            if (line >= Sizes.Count) return Auto[(line - Sizes.Count) % len];
            int j = -line;
            return Auto[(len - j % len) % len];
        }
    }

    /// <summary>
    /// 自动放置的占用表：主轴按「带」分段（同一带内各主轴位置的占用区间相同），每带存次轴区间。
    /// 跨越很多主轴位置的项目只占常数个带边界，内存与跨度的数值无关。
    /// </summary>
    sealed class GridOccupancy
    {
        readonly List<int> _starts = new();
        readonly List<List<int>> _bands = new();
        readonly Stack<List<int>> _free = new();

        public void Reset()
        {
            foreach (var b in _bands)
            {
                b.Clear();
                _free.Push(b);
            }
            _bands.Clear();
            _starts.Clear();
            _starts.Add(int.MinValue);
            _bands.Add(Rent());
        }

        List<int> Rent() => _free.Count > 0 ? _free.Pop() : new List<int>();

        int Find(int major)
        {
            int lo = 0, hi = _starts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_starts[mid] <= major) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        int Split(int major)
        {
            int k = Find(major);
            if (_starts[k] == major) return k;
            var copy = Rent();
            copy.AddRange(_bands[k]);
            _starts.Insert(k + 1, major);
            _bands.Insert(k + 1, copy);
            return k + 1;
        }

        public void Add(int m0, int m1, int n0, int n1)
        {
            int k0 = Split(m0);
            int k1 = Split(m1);
            for (int k = k0; k < k1; k++)
            {
                _bands[k].Add(n0);
                _bands[k].Add(n1);
            }
        }

        /// <summary>
        /// 从 <paramref name="from"/> 起第一个在次轴 [<paramref name="n0"/>, <paramref name="n1"/>) 上没有被占满的主轴位置。
        /// 占用只增不减，所以已占满的位置之后永远占满：dense 放置用它维护单调的起点，避免每个项目都从第一行扫描。
        /// </summary>
        public int SkipFull(int from, int n0, int n1)
        {
            int k = Find(from);
            while (k < _starts.Count)
            {
                if (!Covers(_bands[k], n0, n1)) return Math.Max(from, _starts[k]);
                if (k + 1 >= _starts.Count) return Math.Max(from, _starts[k]);
                from = _starts[k + 1];
                k++;
            }
            return from;
        }

        readonly List<long> _sortScratch = new();

        bool Covers(List<int> b, int n0, int n1)
        {
            if (b.Count == 0 || n1 <= n0) return n1 <= n0;
            var a = _sortScratch;
            a.Clear();
            for (int i = 0; i < b.Count; i += 2)
                if (b[i + 1] > n0 && b[i] < n1) a.Add(((long)b[i] << 32) | (uint)b[i + 1]);
            a.Sort();
            long reach = n0;
            foreach (var x in a)
            {
                int s = (int)(x >> 32), e = (int)(uint)x;
                if (s > reach) return false;
                if (e > reach) reach = e;
                if (reach >= n1) return true;
            }
            return reach >= n1;
        }

        /// <summary>
        /// 矩形是否与已占用的区域冲突：冲突时返回冲突区间中最大的次轴止点（下一个可能不冲突的次轴位置），
        /// 并在 <paramref name="nextMajor"/> 中给出冲突带中最大的主轴止点；不冲突返回 int.MinValue。
        /// </summary>
        public int Collide(int m0, int m1, int n0, int n1, out int nextMajor)
        {
            int res = int.MinValue;
            nextMajor = int.MinValue;
            for (int k = Find(m0); k < _starts.Count && _starts[k] < m1; k++)
            {
                var b = _bands[k];
                bool hit = false;
                for (int i = 0; i < b.Count; i += 2)
                {
                    if (b[i] < n1 && b[i + 1] > n0)
                    {
                        if (b[i + 1] > res) res = b[i + 1];
                        hit = true;
                    }
                }
                if (hit) nextMajor = Math.Max(nextMajor, k + 1 < _starts.Count ? _starts[k + 1] : int.MaxValue);
            }
            return res;
        }
    }

    readonly Stack<GItem[]> _gItemPool = new();
    readonly Stack<GTrack[]> _gTrackPool = new();
    readonly Stack<GridAxis> _gAxisPool = new();
    readonly Stack<GridOccupancy> _gOccPool = new();
    readonly List<int> _gScratchLines = new();
    int[] _gIdx = new int[16];
    float[] _gKeys = new float[16];
    float[] _gCap = new float[16];
    float[] _gWeight = new float[16];
    int[] _gOrd = new int[16];
    readonly List<int> _gTouched = new();

    GItem[] RentGItems(int n)
    {
        var a = _gItemPool.Count > 0 ? _gItemPool.Pop() : new GItem[Math.Max(n, 8)];
        return a.Length >= n ? a : new GItem[Math.Max(n, a.Length * 2)];
    }

    GTrack[] RentGTracks(int n)
    {
        var a = _gTrackPool.Count > 0 ? _gTrackPool.Pop() : new GTrack[Math.Max(n, 8)];
        if (a.Length < n) a = new GTrack[Math.Max(n, a.Length * 2)];
        Array.Clear(a, 0, n);
        return a;
    }

    void ReturnGTracks(GTrack[]? a)
    {
        if (a is not null) _gTrackPool.Push(a);
    }

    GridAxis RentAxis()
    {
        var a = _gAxisPool.Count > 0 ? _gAxisPool.Pop() : new GridAxis();
        a.Reset();
        return a;
    }

    void EnsureScratch(int n)
    {
        if (_gIdx.Length >= n) return;
        int len = Math.Max(n, _gIdx.Length * 2);
        _gIdx = new int[len];
        _gKeys = new float[len];
        _gCap = new float[len];
        _gWeight = new float[len];
        _gOrd = new int[len];
    }

    /// <summary>网格容器布局（CSS Grid 1 §11.1 网格尺寸算法）。</summary>
    LayoutOutput ComputeGrid(LayoutNode node, in LayoutInput input)
    {
        var s = node.Style;
        var pad = ResolvePadding(s, input.ParentWidth);
        var bor = ResolveBorder(s);
        float pbW = pad.Horizontal + bor.Horizontal, pbH = pad.Vertical + bor.Vertical;

        StyleSizes sz;
        bool inherent = input.Sizing == SizingMode.Inherent;
        if (inherent) ResolveSizes(node, input.ParentWidth, input.ParentHeight, pbW, pbH, out sz);
        else sz = StyleSizes.Unconstrained(pbW, pbH);

        float w = Or(input.KnownWidth, sz.ClampedWidth);
        float h = Or(input.KnownHeight, sz.ClampedHeight);
        if (inherent) RatioAsMinHeight(s, ref w, ref h, pbW, pbH, ref sz, input.KnownHeight);

        bool perform = input.IsPerform;
        if (!perform && IsDef(w) && (IsDef(h) || input.WidthOnly) && !input.RequestBaseline) return new LayoutOutput(w, h, float.NaN);

        bool rtl = s.Direction == Direction.Rtl;
        var margin = ResolveMargin(s, input.ParentWidth, out _);
        var children = RentList();
        CollectChildren(node, children, perform);
        SortByOrder(children);

        int count = 0;
        for (int i = 0; i < children.Count; i++)
            if (!children[i].Style.IsOutOfFlow) count++;
        var items = RentGItems(count);
        count = 0;
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            if (c.Style.IsOutOfFlow) continue;
            ref var it = ref items[count++];
            it = default;
            it.Node = c;
            ResolveItemAlignment(s, c, rtl, out it.JAlign, out it.AAlign);
        }

        float minInnerW = Math.Max(0, sz.MinWidth - pbW), maxInnerW = Math.Max(0, sz.MaxWidth - pbW);
        float minInnerH = Math.Max(0, sz.MinHeight - pbH), maxInnerH = Math.Max(0, sz.MaxHeight - pbH);
        float innerW = IsDef(w) ? Math.Max(0, w - pbW) : float.NaN;
        float innerH = IsDef(h) ? Math.Max(0, h - pbH) : float.NaN;

        var colAx = RentAxis();
        var rowAx = RentAxis();
        var occ = _gOccPool.Count > 0 ? _gOccPool.Pop() : new GridOccupancy();
        GTrack[]? cols = null, rows = null;
        int nCols = 0, nRows = 0;
        float baseline = float.NaN;
        try
        {
            bool colStretch = s.JustifyContent is JustifyContent.Normal or JustifyContent.Stretch;
            bool rowStretch = s.AlignContent is AlignContent.Normal or AlignContent.Stretch;

            if (!IsDef(w))
            {
                // 固有宽度：网格在不确定的宽度下建立（auto-repeat 按 max/min-width），百分比列与百分比间距按 auto / 0
                BuildGrid(s, items, count, innerW, innerH, minInnerW, maxInnerW, minInnerH, maxInnerH, colAx, rowAx, occ,
                    out cols, out nCols, out rows, out nRows);
                float gap0 = Math.Max(0, s.ColumnGap.ResolveOrZero(float.NaN));
                var avail = input.AvailableWidth.Shrink(margin.Horizontal + pbW);
                float content;
                switch (avail.Kind)
                {
                    case AvailableSpaceKind.MinContent:
                        content = SizeAxis(cols, nCols, items, count, true, CMin, float.NaN, float.NaN, gap0, minInnerW, maxInnerW, float.NaN);
                        break;
                    case AvailableSpaceKind.MaxContent:
                        content = SizeAxis(cols, nCols, items, count, true, CMax, float.NaN, float.NaN, gap0, minInnerW, maxInnerW, float.NaN);
                        break;
                    default:
                        float maxC = SizeAxis(cols, nCols, items, count, true, CMax, float.NaN, float.NaN, gap0, minInnerW, maxInnerW, float.NaN);
                        if (maxC <= avail.Value) content = maxC;
                        else
                        {
                            float minC = SizeAxis(cols, nCols, items, count, true, CMin, float.NaN, float.NaN, gap0, minInnerW, maxInnerW, float.NaN);
                            content = Math.Min(maxC, Math.Max(minC, avail.Value));
                        }
                        break;
                }
                w = Clamp(content + pbW, sz.MinWidth, sz.MaxWidth);
                if (input.WidthOnly) return new LayoutOutput(w, float.NaN, float.NaN);
                innerW = Math.Max(0, w - pbW);
                ReturnGTracks(cols);
                ReturnGTracks(rows);
                cols = rows = null;
            }

            BuildGrid(s, items, count, innerW, innerH, minInnerW, maxInnerW, minInnerH, maxInnerH, colAx, rowAx, occ,
                out cols, out nCols, out rows, out nRows);

            // 列
            float colGap = Math.Max(0, s.ColumnGap.ResolveOrZero(innerW));
            SizeAxis(cols, nCols, items, count, true, CDef, innerW, innerW, colGap, minInnerW, maxInnerW, colStretch ? innerW : float.NaN);
            PositionTracks(cols, nCols, innerW, colGap, MapJustify(s.JustifyContent, false));

            // 行：项目的行贡献按所在区域（含列的内容分布）的宽度求
            for (int i = 0; i < count; i++)
            {
                ref var it = ref items[i];
                it.AreaW = Math.Max(0, cols[it.C1 - 1].End - cols[it.C0].Pos);
                it.WidthFor = float.NaN;
            }
            float rowGap = Math.Max(0, s.RowGap.ResolveOrZero(innerH));
            float rowStretchTarget = !rowStretch ? float.NaN : IsDef(innerH) ? innerH : minInnerH > 0 ? minInnerH : float.NaN;
            float rowsTotal = SizeAxis(rows, nRows, items, count, false, IsDef(innerH) ? CDef : CMax, innerH, innerH, rowGap,
                minInnerH, maxInnerH, rowStretchTarget);
            if (!IsDef(h))
            {
                h = Clamp(rowsTotal + pbH, sz.MinHeight, sz.MaxHeight);
                innerH = Math.Max(0, h - pbH);
            }
            PositionTracks(rows, nRows, innerH, rowGap, MapAlignContent(s.AlignContent, false));

            // 网格容器的第一条基线：首行（有项目的最上一行）中网格顺序最前的项目（基线对齐未实现）
            int baseItem = -1;
            if (perform || input.RequestBaseline)
            {
                for (int i = 0; i < count; i++)
                {
                    ref var it = ref items[i];
                    if (baseItem < 0 || it.R0 < items[baseItem].R0 || (it.R0 == items[baseItem].R0 && it.C0 < items[baseItem].C0)) baseItem = i;
                }
            }

            float contentLeft = bor.Left + pad.Left, contentTop = bor.Top + pad.Top;
            if (perform)
            {
                float extentR = 0, extentB = 0;
                for (int i = 0; i < count; i++)
                {
                    var o = LayoutGridItem(ref items[i], cols, rows, innerW, contentLeft, contentTop, rtl, RunMode.PerformLayout, false);
                    var c = items[i].Node;
                    AccumulateExtent(c, ref extentR, ref extentB);
                    if (i == baseItem) baseline = c._y + (IsDef(o.Baseline) ? o.Baseline : o.Height);
                }
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    if (!c.Style.IsOutOfFlow) continue;
                    // 静态位置：视作内容盒中唯一的网格项目，起始对齐（§9.2）
                    c._staticX = contentLeft;
                    c._staticY = contentTop;
                    c._staticW = innerW;
                    c._staticH = innerH;
                    c._staticAlignX = rtl ? AlignEnd : AlignStart;
                    c._staticAlignY = AlignStart;
                    SetGridContainingBlock(c, node, colAx, rowAx, cols, nCols, rows, nRows, w, h, in pad, in bor, rtl);
                }
                FinishContainer(node, w, h, baseline, pad, bor, extentR, extentB);
            }
            else if (input.RequestBaseline && baseItem >= 0)
            {
                ref var it = ref items[baseItem];
                var o = LayoutGridItem(ref it, cols, rows, innerW, contentLeft, contentTop, rtl, RunMode.ComputeSize, true);
                baseline = _gLastItemY + (IsDef(o.Baseline) ? o.Baseline : o.Height);
            }
            return new LayoutOutput(w, h, baseline);
        }
        finally
        {
            ReturnGTracks(cols);
            ReturnGTracks(rows);
            _gAxisPool.Push(colAx);
            _gAxisPool.Push(rowAx);
            _gOccPool.Push(occ);
            Array.Clear(items, 0, count);
            _gItemPool.Push(items);
            ReturnList(children);
        }
    }

    float _gLastItemY;

    /// <summary>
    /// justify-self / align-self 解析为逻辑方向的对齐。<c>normal</c>（§6.2）：没有宽高比的非替换元素按 stretch；
    /// 否则按块级元素的尺寸规则——行内轴上非替换元素撑满（stretch）、替换元素取固有宽度（start），块轴上高度为 auto（由宽高比推出，start）。
    /// 替换元素由 <see cref="LayoutStyle.IsReplaced"/> 判断。
    /// </summary>
    static void ResolveItemAlignment(LayoutStyle container, LayoutNode item, bool rtl, out byte j, out byte a)
    {
        var cs = item.Style;
        bool ratio = cs.IsReplaced || cs.AspectRatio is not null;
        var js = cs.JustifySelf;
        if (js == JustifySelf.Auto)
        {
            js = container.JustifyItems switch
            {
                JustifyItems.Stretch => JustifySelf.Stretch,
                JustifyItems.Start => JustifySelf.Start,
                JustifyItems.End => JustifySelf.End,
                JustifyItems.Center => JustifySelf.Center,
                JustifyItems.Left => JustifySelf.Left,
                JustifyItems.Right => JustifySelf.Right,
                JustifyItems.FlexStart => JustifySelf.FlexStart,
                JustifyItems.FlexEnd => JustifySelf.FlexEnd,
                JustifyItems.SelfStart => JustifySelf.SelfStart,
                JustifyItems.SelfEnd => JustifySelf.SelfEnd,
                JustifyItems.Baseline => JustifySelf.Baseline,
                _ => JustifySelf.Normal,
            };
        }
        j = js switch
        {
            JustifySelf.Normal => cs.IsReplaced ? ItemStart : ItemStretch,
            JustifySelf.Stretch => ItemStretch,
            JustifySelf.End or JustifySelf.FlexEnd or JustifySelf.SelfEnd => ItemEnd,
            JustifySelf.Center => ItemCenter,
            JustifySelf.Left => rtl ? ItemEnd : ItemStart,
            JustifySelf.Right => rtl ? ItemStart : ItemEnd,
            _ => ItemStart,
        };
        var al = cs.AlignSelf switch
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
        a = al switch
        {
            AlignItems.Normal => ratio ? ItemStart : ItemStretch,
            AlignItems.Stretch => ItemStretch,
            AlignItems.End or AlignItems.FlexEnd => ItemEnd,
            AlignItems.Center => ItemCenter,
            _ => ItemStart,
        };
    }

    // ======================================================================
    // 显式网格与放置（§7、§8）
    // ======================================================================

    void BuildGrid(LayoutStyle s, GItem[] items, int count, float innerW, float innerH, float minW, float maxW, float minH, float maxH,
        GridAxis colAx, GridAxis rowAx, GridOccupancy occ, out GTrack[] cols, out int nCols, out GTrack[] rows, out int nRows)
    {
        var areas = s.GridTemplateAreas;
        ExpandAxis(colAx, s.GridTemplateColumns, s.GridAutoColumns, areas, false, innerW, minW, maxW, s.ColumnGap);
        ExpandAxis(rowAx, s.GridTemplateRows, s.GridAutoRows, areas, true, innerH, minH, maxH, s.RowGap);
        PlaceItems(s, items, count, colAx, rowAx, occ);

        nCols = Math.Max(1, colAx.Max - colAx.Min);
        nRows = Math.Max(1, rowAx.Max - rowAx.Min);
        cols = BuildTracks(colAx, nCols, items, count, true);
        rows = BuildTracks(rowAx, nRows, items, count, false);
    }

    GTrack[] BuildTracks(GridAxis ax, int n, GItem[] items, int count, bool columns)
    {
        var t = RentGTracks(n);
        bool anyAutoFit = false;
        for (int i = 0; i < n; i++)
        {
            int line = i + ax.Min;
            t[i].Fn = ax.SizeOf(line);
            if (line >= 0 && line < ax.AutoFit.Count && ax.AutoFit[line])
            {
                t[i].AutoFit = true;
                anyAutoFit = true;
            }
        }
        // 项目坐标换算为轨道下标
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            if (columns)
            {
                it.C0 -= ax.Min;
                it.C1 -= ax.Min;
            }
            else
            {
                it.R0 -= ax.Min;
                it.R1 -= ax.Min;
            }
        }
        if (anyAutoFit)
        {
            // auto-fit：没有项目的重复轨道折叠（差分数组求每条轨道上的项目数）
            var occupied = new int[n + 1];
            for (int i = 0; i < count; i++)
            {
                int a = columns ? items[i].C0 : items[i].R0, b = columns ? items[i].C1 : items[i].R1;
                occupied[a]++;
                occupied[b]--;
            }
            int run = 0;
            for (int i = 0; i < n; i++)
            {
                run += occupied[i];
                if (t[i].AutoFit && run == 0) t[i].Collapsed = true;
            }
        }
        return t;
    }

    /// <summary>展开轨道列表（auto-repeat 次数按 §7.2.3.2），记录线名，并加入区域定义的轨道。</summary>
    static void ExpandAxis(GridAxis ax, GridTrackList? template, GridTrackList? auto, GridAreas? areas, bool isRow,
        float size, float min, float max, LengthPercentage gapStyle)
    {
        ax.Reset();
        ax.IsRow = isRow;
        ax.Areas = areas;
        ax.Auto = FlattenAuto(auto);
        if (template is not null)
        {
            int reps = template.HasAutoRepeat ? AutoRepeatCount(template, size, min, max, gapStyle) : 1;
            bool autoUsed = false;
            bool names = template.HasNames;
            int line = 0;
            foreach (var seg in template.SegmentArray)
            {
                int times = seg.Kind == GridRepeatKind.Count ? seg.Count : !autoUsed ? reps : 1;
                if (seg.Kind != GridRepeatKind.Count) autoUsed = true;
                var tracks = seg.TrackArray;
                var segNames = seg.NameArray;
                bool fit = seg.Kind == GridRepeatKind.AutoFit;
                if (tracks.Length == 0)
                {
                    if (names) foreach (var nm in segNames[0]) ax.AddName(nm, line);
                    continue;
                }
                for (int k = 0; k < times && ax.Sizes.Count < GridLine.MaxLine - 1; k++)
                {
                    for (int j = 0; j < tracks.Length; j++)
                    {
                        if (names) foreach (var nm in segNames[j]) ax.AddName(nm, line);
                        if (ax.Sizes.Count >= GridLine.MaxLine - 1) break;
                        ax.Sizes.Add(tracks[j]);
                        ax.AutoFit.Add(fit);
                        line++;
                    }
                    if (names) foreach (var nm in segNames[tracks.Length]) ax.AddName(nm, line);
                }
            }
        }
        int fromAreas = areas is null ? 0 : Math.Min(isRow ? areas.Rows : areas.Columns, GridLine.MaxLine - 1);
        // 只由区域定义、未由轨道列表给出尺寸的轨道取 grid-auto-* 的尺寸
        for (int j = 0; ax.Sizes.Count < fromAreas; j++)
        {
            ax.Sizes.Add(ax.Auto.Length == 0 ? GridTrackSize.Auto : ax.Auto[j % ax.Auto.Length]);
            ax.AutoFit.Add(false);
        }
        ax.Explicit = ax.Sizes.Count;
    }

    static GridTrackSize[] FlattenAuto(GridTrackList? auto)
    {
        if (auto is null) return Array.Empty<GridTrackSize>();
        var list = new List<GridTrackSize>();
        foreach (var seg in auto.SegmentArray)
            foreach (var t in seg.TrackArray)
                if (list.Count < GridLine.MaxLine) list.Add(t);
        return list.ToArray();
    }

    /// <summary>
    /// auto-fill / auto-fit 的重复次数（§7.2.3.2）：确定尺寸（或最大尺寸）下不溢出的最大正整数；只有最小尺寸时为满足它的最小正整数；
    /// 否则 1。每条轨道按其确定的最大尺寸函数（否则最小尺寸函数）计，重复轨道至少按 1px。
    /// </summary>
    static int AutoRepeatCount(GridTrackList list, float size, float min, float max, LengthPercentage gapStyle)
    {
        float avail;
        bool isMin = false;
        if (IsDef(size)) avail = size;
        else if (!float.IsPositiveInfinity(max)) avail = Math.Max(max, min);
        else if (min > 0)
        {
            avail = min;
            isMin = true;
        }
        else return 1;
        float gap = Math.Max(0, gapStyle.ResolveOrZero(avail));
        double fixedSum = 0, repSum = 0;
        long fixedCount = 0, repCount = 0;
        bool autoSeen = false;
        foreach (var seg in list.SegmentArray)
        {
            bool isAuto = seg.Kind != GridRepeatKind.Count && !autoSeen;
            if (seg.Kind != GridRepeatKind.Count) autoSeen = true;
            int times = isAuto ? 1 : seg.Count;
            foreach (var t in seg.TrackArray)
            {
                float v = FixedTrackSize(t, avail);
                if (isAuto)
                {
                    repSum += Math.Max(1, v);
                    repCount++;
                }
                else
                {
                    fixedSum += (double)v * times;
                    fixedCount += times;
                }
            }
        }
        if (repCount == 0) return 1;
        double room = avail - fixedSum - (fixedCount - 1) * (double)gap;
        double per = repSum + repCount * (double)gap;
        double n = isMin ? Math.Ceiling(room / per) : Math.Floor(room / per);
        long cap = Math.Max(1, (GridLine.MaxLine - 1 - fixedCount) / repCount);
        if (double.IsNaN(n) || n < 1) return 1;
        return (int)Math.Min(n, cap);
    }

    static float FixedTrackSize(GridTrackSize t, float basis)
    {
        if (!t.IsFitContent && t.Max.Kind == GridBreadthKind.Length)
        {
            float v = t.Max.Length.ResolveOrNaN(basis);
            if (IsDef(v)) return Math.Max(0, Lim(v));
        }
        if (t.Min.Kind == GridBreadthKind.Length)
        {
            float v = t.Min.Length.ResolveOrNaN(basis);
            if (IsDef(v)) return Math.Max(0, Lim(v));
        }
        return 0;
    }

    /// <summary>§8.3 按线放置 + §8.5 自动放置。结果写入项目（显式网格坐标）与两个轴的范围。</summary>
    void PlaceItems(LayoutStyle s, GItem[] items, int count, GridAxis colAx, GridAxis rowAx, GridOccupancy occ)
    {
        bool colFlow = (s.GridAutoFlow & GridAutoFlow.Column) != 0;
        bool dense = (s.GridAutoFlow & GridAutoFlow.RowDense) != 0;
        int cMin = 0, cMax = colAx.Explicit, rMin = 0, rMax = rowAx.Explicit;

        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            var cs = it.Node.Style;
            it.ColDef = ResolveSpan(cs.GridColumnStart, cs.GridColumnEnd, colAx, out it.C0, out it.C1, out it.ColSpan);
            it.RowDef = ResolveSpan(cs.GridRowStart, cs.GridRowEnd, rowAx, out it.R0, out it.R1, out it.RowSpan);
        }

        // 以「主轴」= 自动放置光标逐行（逐列）前进的轴来写：row 流向时主轴是行，次轴是列
        occ.Reset();
        int minorMin = colFlow ? rMin : cMin, minorMax = colFlow ? rMax : cMax;
        int majorMin = colFlow ? cMin : rMin, majorMax = colFlow ? cMax : rMax;
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            if (it.RowDef)
            {
                if (colFlow) { minorMin = Math.Min(minorMin, it.R0); minorMax = Math.Max(minorMax, it.R1); }
                else { majorMin = Math.Min(majorMin, it.R0); majorMax = Math.Max(majorMax, it.R1); }
            }
            if (it.ColDef)
            {
                if (colFlow) { majorMin = Math.Min(majorMin, it.C0); majorMax = Math.Max(majorMax, it.C1); }
                else { minorMin = Math.Min(minorMin, it.C0); minorMax = Math.Max(minorMax, it.C1); }
            }
            if (it.RowDef && it.ColDef)
            {
                if (colFlow) occ.Add(it.C0, it.C1, it.R0, it.R1);
                else occ.Add(it.R0, it.R1, it.C0, it.C1);
            }
        }

        // 第 1 步：锁定在某一行（列）的项目
        Dictionary<int, int>? rowCursors = null;
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            GetMajorMinor(ref it, colFlow, out bool majDef, out bool minDef, out int m0, out int m1, out _, out int minorSpan);
            if (!majDef || minDef) continue;
            int start = 0;
            if (!dense)
            {
                rowCursors ??= new Dictionary<int, int>();
                rowCursors.TryGetValue(m0, out start);
            }
            int n0 = start;
            while (true)
            {
                if (n0 + minorSpan > GridLine.MaxLine)
                {
                    n0 = GridLine.MaxLine - minorSpan;
                    break;
                }
                int hit = occ.Collide(m0, m1, n0, n0 + minorSpan, out _);
                if (hit == int.MinValue) break;
                n0 = Math.Max(n0 + 1, hit);
            }
            SetMinor(ref it, colFlow, n0, n0 + minorSpan);
            if (rowCursors is not null) rowCursors[m0] = n0 + minorSpan;
            occ.Add(m0, m1, n0, n0 + minorSpan);
            minorMin = Math.Min(minorMin, n0);
            minorMax = Math.Max(minorMax, n0 + minorSpan);
        }

        // 第 2 步：次轴的隐式网格要容纳最宽的自动项目
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            GetMajorMinor(ref it, colFlow, out _, out bool minDef, out _, out _, out _, out int minorSpan);
            if (!minDef) minorMax = Math.Max(minorMax, Math.Min(minorMin + minorSpan, GridLine.MaxLine));
        }

        // 第 3 步：其余项目（dense 时从 denseFloor 起找：之前的主轴位置都已占满）
        int curMajor = majorMin, curMinor = minorMin;
        int denseFloor = majorMin;
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            GetMajorMinor(ref it, colFlow, out bool majDef, out bool minDef, out int m0, out int m1, out int majorSpan, out int minorSpan);
            if (majDef) continue;
            if (minDef)
            {
                // 次轴位置确定：在该位置上沿主轴找第一个不冲突的位置
                int n0 = colFlow ? it.R0 : it.C0, n1 = colFlow ? it.R1 : it.C1;
                if (dense) curMajor = denseFloor = occ.SkipFull(denseFloor, minorMin, minorMax);
                else if (n0 < curMinor) curMajor++;
                curMinor = n0;
                int maj = curMajor;
                while (true)
                {
                    if (maj + majorSpan > GridLine.MaxLine)
                    {
                        maj = GridLine.MaxLine - majorSpan;
                        break;
                    }
                    int hit = occ.Collide(maj, maj + majorSpan, n0, n1, out int nextMajor);
                    if (hit == int.MinValue) break;
                    // 冲突带之内的任何起点都会冲突：直接跳到冲突带的末端
                    maj = Math.Max(maj + 1, Math.Min(nextMajor, GridLine.MaxLine));
                }
                curMajor = maj;
                SetMajor(ref it, colFlow, maj, maj + majorSpan);
                occ.Add(maj, maj + majorSpan, n0, n1);
            }
            else
            {
                if (dense)
                {
                    curMajor = denseFloor = occ.SkipFull(denseFloor, minorMin, minorMax);
                    curMinor = minorMin;
                }
                int maj = curMajor, n0 = curMinor;
                while (true)
                {
                    if (maj + majorSpan > GridLine.MaxLine)
                    {
                        maj = GridLine.MaxLine - majorSpan;
                        n0 = minorMin;
                        break;
                    }
                    if (n0 + minorSpan <= minorMax)
                    {
                        int hit = occ.Collide(maj, maj + majorSpan, n0, n0 + minorSpan, out _);
                        if (hit == int.MinValue) break;
                        n0 = Math.Max(n0 + 1, hit);
                        continue;
                    }
                    maj++;
                    n0 = minorMin;
                }
                curMajor = maj;
                curMinor = n0;
                SetMajor(ref it, colFlow, maj, maj + majorSpan);
                SetMinor(ref it, colFlow, n0, n0 + minorSpan);
                occ.Add(maj, maj + majorSpan, n0, n0 + minorSpan);
            }
            majorMax = Math.Max(majorMax, (colFlow ? it.C1 : it.R1));
            majorMin = Math.Min(majorMin, (colFlow ? it.C0 : it.R0));
        }

        if (colFlow)
        {
            colAx.Min = majorMin; colAx.Max = majorMax; rowAx.Min = minorMin; rowAx.Max = minorMax;
        }
        else
        {
            rowAx.Min = majorMin; rowAx.Max = majorMax; colAx.Min = minorMin; colAx.Max = minorMax;
        }
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            colAx.Min = Math.Min(colAx.Min, it.C0); colAx.Max = Math.Max(colAx.Max, it.C1);
            rowAx.Min = Math.Min(rowAx.Min, it.R0); rowAx.Max = Math.Max(rowAx.Max, it.R1);
        }
    }

    static void GetMajorMinor(ref GItem it, bool colFlow, out bool majDef, out bool minDef, out int m0, out int m1, out int majorSpan, out int minorSpan)
    {
        if (colFlow)
        {
            majDef = it.ColDef; minDef = it.RowDef; m0 = it.C0; m1 = it.C1; majorSpan = it.ColSpan; minorSpan = it.RowSpan;
        }
        else
        {
            majDef = it.RowDef; minDef = it.ColDef; m0 = it.R0; m1 = it.R1; majorSpan = it.RowSpan; minorSpan = it.ColSpan;
        }
    }

    static void SetMajor(ref GItem it, bool colFlow, int a, int b)
    {
        if (colFlow) { it.C0 = a; it.C1 = b; it.ColDef = true; }
        else { it.R0 = a; it.R1 = b; it.RowDef = true; }
    }

    static void SetMinor(ref GItem it, bool colFlow, int a, int b)
    {
        if (colFlow) { it.R0 = a; it.R1 = b; it.RowDef = true; }
        else { it.C0 = a; it.C1 = b; it.ColDef = true; }
    }

    /// <summary>
    /// 一个轴的两条放置线（§8.3）：返回位置是否确定；确定时给出 [a, b)（显式网格的 0 起线下标，可为负或越过显式网格），
    /// 否则给出自动放置用的跨度。
    /// </summary>
    bool ResolveSpan(GridLine start, GridLine end, GridAxis ax, out int a, out int b, out int span)
    {
        a = b = 0;
        span = 1;
        bool sLine = start.Kind == GridLineKind.Line, eLine = end.Kind == GridLineKind.Line;
        if (!sLine && !eLine)
        {
            // 自动位置：命名的 span 按 1；两个都是 span 时忽略结束侧
            if (start.IsSpan) span = start.Name is null ? start.Integer : 1;
            else if (end.IsSpan) span = end.Name is null ? end.Integer : 1;
            span = Math.Max(1, Math.Min(span, GridLine.MaxLine));
            return false;
        }
        if (sLine && eLine)
        {
            a = ResolveLine(start, true, ax);
            b = ResolveLine(end, false, ax);
            if (a > b) (a, b) = (b, a);
            if (a == b) b = a + 1;
        }
        else if (sLine)
        {
            a = ResolveLine(start, true, ax);
            b = end.IsSpan ? SpanForward(a, end, ax) : a + 1;
        }
        else
        {
            b = ResolveLine(end, false, ax);
            a = start.IsSpan ? SpanBackward(b, start, ax) : b - 1;
        }
        a = Math.Max(-GridLine.MaxLine, Math.Min(a, GridLine.MaxLine - 1));
        b = Math.Max(a + 1, Math.Min(b, GridLine.MaxLine));
        span = b - a;
        return true;
    }

    /// <summary>把一条 <c>&lt;grid-line&gt;</c> 解析为显式网格的 0 起线下标（第 1 条线为 0）。</summary>
    int ResolveLine(GridLine g, bool isStart, GridAxis ax)
    {
        int n = ax.Explicit;
        if (g.Name is null) return g.Integer > 0 ? g.Integer - 1 : n + 1 + g.Integer;
        int k = g.Integer;
        if (k == 0)
        {
            // 只有名字：先找 名字-start / 名字-end，再按「1 名字」
            var edge = ax.Lines(g.Name + (isStart ? "-start" : "-end"), _gScratchLines);
            if (edge is { Count: > 0 }) return edge[0];
            k = 1;
        }
        var lines = ax.Lines(g.Name, _gScratchLines);
        int have = lines?.Count ?? 0;
        if (k > 0)
        {
            if (have >= k) return lines![k - 1];
            return n + (k - have);   // 名字不够时，显式网格之后的隐式线都视作有该名字
        }
        k = -k;
        if (have >= k) return lines![have - k];
        return -(k - have);
    }

    int SpanForward(int from, GridLine g, GridAxis ax)
    {
        int k = g.Integer;
        if (g.Name is null) return from + k;
        var lines = ax.Lines(g.Name, _gScratchLines);
        int found = 0;
        if (lines is not null)
        {
            int i = lines.BinarySearch(from + 1);
            if (i < 0) i = ~i;
            for (; i < lines.Count; i++)
                if (++found == k) return lines[i];
        }
        return Math.Max(from, ax.Explicit) + (k - found);
    }

    int SpanBackward(int from, GridLine g, GridAxis ax)
    {
        int k = g.Integer;
        if (g.Name is null) return from - k;
        var lines = ax.Lines(g.Name, _gScratchLines);
        int found = 0;
        if (lines is not null)
        {
            int i = lines.BinarySearch(from);
            if (i < 0) i = ~i;
            for (i--; i >= 0; i--)
                if (++found == k) return lines[i];
        }
        return Math.Min(from, 0) - (k - found);
    }

    // ======================================================================
    // 轨道尺寸算法（§11.3 – §11.8）
    // ======================================================================

    /// <summary>
    /// 一个轴的轨道尺寸算法。<paramref name="avail"/> 为确定的可用空间（<see cref="CDef"/>）或 NaN；
    /// <paramref name="pct"/> 为百分比基准（NaN 时百分比轨道按 auto）。返回轨道总尺寸（含间距）。
    /// </summary>
    float SizeAxis(GTrack[] t, int n, GItem[] items, int count, bool columns, byte constraint, float avail, float pct, float gap,
        float minInner, float maxInner, float stretchTarget)
    {
        for (int i = 0; i < n; i++) InitTrack(ref t[i], pct);
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            it.MinC = it.MaxC = it.Minimum = float.NaN;
            int a = columns ? it.C0 : it.R0, b = columns ? it.C1 : it.R1;
            bool autoMin = false, flex = false, fixedMax = true;
            float maxSum = 0;
            int visible = 0;
            for (int k = a; k < b; k++)
            {
                if (t[k].MinK == TkAuto) autoMin = true;
                if (t[k].MaxK == TkFlex) flex = true;
                if (t[k].MaxK != TkFixed) fixedMax = false;
                maxSum += t[k].MaxV;
                if (!t[k].Collapsed) visible++;
            }
            it.SpansAutoMin = autoMin;
            it.CrossesFlex = flex;
            it.FixedMax = fixedMax ? maxSum + gap * Math.Max(0, visible - 1) : float.PositiveInfinity;
        }

        // §11.5 第 2 步：跨一条轨道（且不跨 fr 轨道）的项目
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            int a = columns ? it.C0 : it.R0, b = columns ? it.C1 : it.R1;
            if (b - a != 1 || it.CrossesFlex) continue;
            ref var tr = ref t[a];
            if (tr.MinK == TkFixed && tr.MaxK == TkFixed) continue;
            switch (tr.MinK)
            {
                case TkMinC: tr.Base = Math.Max(tr.Base, Contribution(ref it, columns, 0)); break;
                case TkMaxC: tr.Base = Math.Max(tr.Base, Contribution(ref it, columns, 1)); break;
                case TkAuto:
                    tr.Base = Math.Max(tr.Base, constraint != CDef ? Limited(ref it, LimitSum(t, a, b), columns, 0) : Contribution(ref it, columns, 2));
                    break;
            }
            float lim = float.NaN;
            switch (tr.MaxK)
            {
                case TkMinC: lim = Contribution(ref it, columns, 0); break;
                case TkMaxC:
                case TkAuto: lim = Contribution(ref it, columns, 1); break;
                case TkFit: lim = Math.Min(Contribution(ref it, columns, 1), Math.Max(tr.MaxV, tr.Base)); break;
            }
            if (IsDef(lim))
            {
                if (!tr.Touched) { tr.Limit = lim; tr.Touched = true; }
                else tr.Limit = Math.Max(tr.Limit, lim);
            }
        }
        for (int i = 0; i < n; i++)
        {
            t[i].Touched = false;
            if (t[i].Limit < t[i].Base) t[i].Limit = t[i].Base;
        }

        // 第 3 步：跨多条轨道、不跨 fr 轨道的项目，按跨度从小到大分组
        int spanning = 0;
        var order = Array.Empty<int>();
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            int span = columns ? it.C1 - it.C0 : it.R1 - it.R0;
            if (span > 1 && !it.CrossesFlex) spanning++;
        }
        if (spanning > 0)
        {
            order = new int[spanning];
            var keys = new long[spanning];
            int k = 0;
            for (int i = 0; i < count; i++)
            {
                ref var it = ref items[i];
                int span = columns ? it.C1 - it.C0 : it.R1 - it.R0;
                if (span <= 1 || it.CrossesFlex) continue;
                // 同一跨度内按起点排序：起止都相同的项目相邻，分配时合并（见 DistributeRuns）
                keys[k] = ((long)span << 32) | (uint)(columns ? it.C0 : it.R0);
                order[k++] = i;
            }
            Array.Sort(keys, order);
            int g0 = 0;
            while (g0 < spanning)
            {
                int span = columns ? items[order[g0]].C1 - items[order[g0]].C0 : items[order[g0]].R1 - items[order[g0]].R0;
                int g1 = g0;
                while (g1 < spanning && (columns ? items[order[g1]].C1 - items[order[g1]].C0 : items[order[g1]].R1 - items[order[g1]].R0) == span) g1++;
                // 先求出本组全部贡献（会递归布局子节点），再分配：分配阶段使用引擎的共享临时状态，不能被递归调用打断
                for (int q = g0; q < g1; q++) PrecomputeContributions(ref items[order[q]], columns);
                for (int phase = 1; phase <= 5; phase++)
                {
                    DistributeRuns(t, items, order, g0, g1, columns, phase, constraint, false, gap);
                    ApplyPlanned(t, phase);
                }
                for (int i = 0; i < n; i++) t[i].InfGrow = false;
                g0 = g1;
            }
        }

        // 第 4 步：跨 fr 轨道的项目（含跨一条的），只把空间分给 fr 轨道，按伸缩因子比例
        int flexItems = 0;
        for (int i = 0; i < count; i++)
            if (items[i].CrossesFlex) flexItems++;
        var flexOrder = Array.Empty<int>();
        if (flexItems > 0)
        {
            // 按 [起, 止) 排序，使范围相同的项目相邻
            flexOrder = new int[flexItems];
            var keys = new long[flexItems];
            int k = 0;
            for (int i = 0; i < count; i++)
            {
                ref var it = ref items[i];
                if (!it.CrossesFlex) continue;
                PrecomputeContributions(ref it, columns);
                keys[k] = ((long)(columns ? it.C0 : it.R0) << 32) | (uint)(columns ? it.C1 : it.R1);
                flexOrder[k++] = i;
            }
            Array.Sort(keys, flexOrder);
            for (int phase = 1; phase <= 3; phase++)
            {
                DistributeRuns(t, items, flexOrder, 0, flexItems, columns, phase, constraint, true, gap);
                ApplyPlanned(t, phase);
            }
        }

        // 第 5 步：无限的增长上限取基准尺寸
        for (int i = 0; i < n; i++)
            if (float.IsPositiveInfinity(t[i].Limit) || t[i].Limit < t[i].Base) t[i].Limit = t[i].Base;

        // §11.6 最大化轨道
        float gaps = gap * Math.Max(0, CountVisible(t, n) - 1);
        if (constraint == CMax)
        {
            float total = gaps;
            for (int i = 0; i < n; i++) total += t[i].Limit;
            if (!float.IsPositiveInfinity(maxInner) && total > maxInner) GrowToLimits(t, n, maxInner - gaps);
            else
                for (int i = 0; i < n; i++) t[i].Base = t[i].Limit;
        }
        else if (constraint == CDef) GrowToLimits(t, n, avail - gaps);

        // §11.7 扩展 fr 轨道
        bool anyFlex = false;
        for (int i = 0; i < n && !anyFlex; i++) anyFlex = t[i].MaxK == TkFlex;
        if (anyFlex && constraint != CMin)
        {
            float frac;
            if (constraint == CDef) frac = FindFrSize(t, 0, n, avail, gap);
            else
            {
                frac = 0;
                for (int i = 0; i < n; i++)
                    if (t[i].MaxK == TkFlex) frac = Math.Max(frac, t[i].Flex > 1 ? t[i].Base / t[i].Flex : t[i].Base);
                // 范围相同的项目只需按最大的 max-content 贡献求一次（结果随要填满的空间单调不减）
                for (int q = 0; q < flexItems;)
                {
                    ref var first = ref items[flexOrder[q]];
                    int a = columns ? first.C0 : first.R0, b = columns ? first.C1 : first.R1;
                    float c = 0;
                    for (; q < flexItems; q++)
                    {
                        ref var it = ref items[flexOrder[q]];
                        if ((columns ? it.C0 : it.R0) != a || (columns ? it.C1 : it.R1) != b) break;
                        c = Math.Max(c, Contribution(ref it, columns, 1));
                    }
                    frac = Math.Max(frac, FindFrSize(t, a, b, c, gap));
                }
                float sum = SumWithFraction(t, n, frac) + gaps;
                if (sum < minInner) frac = FindFrSize(t, 0, n, minInner, gap);
                else if (!float.IsPositiveInfinity(maxInner) && sum > maxInner) frac = FindFrSize(t, 0, n, maxInner, gap);
            }
            if (frac > 0 && !float.IsInfinity(frac))
                for (int i = 0; i < n; i++)
                    if (t[i].MaxK == TkFlex) t[i].Base = Math.Max(t[i].Base, Lim(frac * t[i].Flex));
        }

        // §11.8 拉伸 auto 轨道
        float used = gaps;
        for (int i = 0; i < n; i++) used += t[i].Base;
        if (IsDef(stretchTarget) && stretchTarget - used > 0.0001f)
        {
            int autos = 0;
            for (int i = 0; i < n; i++)
                if (t[i].MaxK == TkAuto && !t[i].Collapsed) autos++;
            if (autos > 0)
            {
                float add = (stretchTarget - used) / autos;
                for (int i = 0; i < n; i++)
                    if (t[i].MaxK == TkAuto && !t[i].Collapsed) t[i].Base += add;
                used = stretchTarget;
            }
        }
        return used;
    }

    static int CountVisible(GTrack[] t, int n)
    {
        int c = 0;
        for (int i = 0; i < n; i++)
            if (!t[i].Collapsed) c++;
        return c;
    }

    static float SumWithFraction(GTrack[] t, int n, float frac)
    {
        float sum = 0;
        for (int i = 0; i < n; i++) sum += t[i].MaxK == TkFlex ? Math.Max(t[i].Base, frac * t[i].Flex) : t[i].Base;
        return sum;
    }

    static void InitTrack(ref GTrack t, float pct)
    {
        t.Planned = t.Inc = 0;
        t.InfGrow = t.Touched = t.Inflexible = false;
        t.Flex = 0;
        if (t.Collapsed)
        {
            t.MinK = t.MaxK = TkFixed;
            t.MinV = t.MaxV = t.Base = t.Limit = 0;
            return;
        }
        var f = t.Fn;
        t.MinV = t.MaxV = 0;
        switch (f.Min.Kind)
        {
            case GridBreadthKind.Length:
                float v = f.Min.Length.ResolveOrNaN(pct);
                if (IsDef(v)) { t.MinK = TkFixed; t.MinV = Math.Max(0, Lim(v)); }
                else t.MinK = TkAuto;
                break;
            case GridBreadthKind.MinContent: t.MinK = TkMinC; break;
            case GridBreadthKind.MaxContent: t.MinK = TkMaxC; break;
            default: t.MinK = TkAuto; break;
        }
        if (f.IsFitContent)
        {
            float v = f.Max.Length.ResolveOrNaN(pct);
            if (IsDef(v)) { t.MaxK = TkFit; t.MaxV = Math.Max(0, Lim(v)); }
            else t.MaxK = TkMaxC;
        }
        else
        {
            switch (f.Max.Kind)
            {
                case GridBreadthKind.Length:
                    float v = f.Max.Length.ResolveOrNaN(pct);
                    if (IsDef(v)) { t.MaxK = TkFixed; t.MaxV = Math.Max(0, Lim(v)); }
                    else t.MaxK = TkAuto;
                    break;
                case GridBreadthKind.Flex: t.MaxK = TkFlex; t.Flex = f.Max.Flex; break;
                case GridBreadthKind.MinContent: t.MaxK = TkMinC; break;
                case GridBreadthKind.MaxContent: t.MaxK = TkMaxC; break;
                default: t.MaxK = TkAuto; break;
            }
        }
        t.Base = t.MinK == TkFixed ? t.MinV : 0;
        t.Limit = t.MaxK == TkFixed ? t.MaxV : float.PositiveInfinity;
        if (t.Limit < t.Base) t.Limit = t.Base;
    }

    void PrecomputeContributions(ref GItem it, bool columns)
    {
        Contribution(ref it, columns, 0);
        Contribution(ref it, columns, 1);
        Contribution(ref it, columns, 2);
    }

    /// <summary>
    /// 对 <paramref name="order"/>[q0, q1) 中的项目（起止相同的相邻）做一个阶段的分配。起止相同的一串项目只按其中最大的贡献分配一次：
    /// 项目引起的增量随贡献单调不减，而计划增量取各项目增量的最大值，结果不变；这样许多相同放置的项目（不可信 CSS 中
    /// <c>* { grid-area: 1 / 1 / -1 / -1 }</c>）不会使分配的开销乘以跨度。
    /// </summary>
    void DistributeRuns(GTrack[] t, GItem[] items, int[] order, int q0, int q1, bool columns, int phase, byte constraint, bool flexMode, float gap)
    {
        bool limited = (phase == 1 && constraint != CDef) || (phase == 3 && constraint == CMax);
        int q = q0;
        while (q < q1)
        {
            ref var first = ref items[order[q]];
            int a = columns ? first.C0 : first.R0, b = columns ? first.C1 : first.R1;
            float limit = limited ? LimitSum(t, a, b) : float.PositiveInfinity;
            float c = 0;
            for (; q < q1; q++)
            {
                ref var it = ref items[order[q]];
                if ((columns ? it.C0 : it.R0) != a || (columns ? it.C1 : it.R1) != b) break;
                c = Math.Max(c, PhaseContribution(ref it, limit, columns, phase, constraint));
            }
            DistributeExtra(t, a, b, c, phase, constraint, flexMode, gap);
        }
    }

    float PhaseContribution(ref GItem it, float limit, bool columns, int phase, byte constraint) => phase switch
    {
        1 => constraint != CDef ? Limited(ref it, limit, columns, 0) : Contribution(ref it, columns, 2),
        2 => Contribution(ref it, columns, 0),
        3 => constraint == CMax ? Limited(ref it, limit, columns, 1) : Contribution(ref it, columns, 1),
        4 => Contribution(ref it, columns, 0),
        _ => Contribution(ref it, columns, 1),
    };

    /// <summary>轨道 [a, b) 的最大尺寸函数都固定（含 fit-content 的上限）时为其和，否则 +∞（受限贡献的上限）。</summary>
    static float LimitSum(GTrack[] t, int a, int b)
    {
        float limit = 0;
        for (int k = a; k < b; k++)
        {
            if (t[k].MaxK is TkFixed or TkFit) limit += t[k].MaxV;
            else return float.PositiveInfinity;
        }
        return limit;
    }

    /// <summary>
    /// 受限的 min/max-content 贡献（§11.5）：项目只跨越最大尺寸函数固定（含 fit-content 上限）的轨道时，
    /// 不超过这些上限之和（<paramref name="limit"/>，见 <see cref="LimitSum"/>），并以最小贡献为下限。
    /// </summary>
    float Limited(ref GItem it, float limit, bool columns, int kind)
    {
        float c = Contribution(ref it, columns, kind);
        if (float.IsPositiveInfinity(limit)) return c;
        return Math.Max(Math.Min(c, limit), Contribution(ref it, columns, 2));
    }

    /// <summary>
    /// 项目在一个轴上的外尺寸贡献：<paramref name="kind"/> 0 = min-content，1 = max-content，2 = 最小贡献（§6.6 自动最小尺寸）。
    /// 列：项目的百分比尺寸与外边距按不确定处理；行：按区域宽度确定项目宽度后求高度（块向的 min/max-content 相同）。
    /// </summary>
    float Contribution(ref GItem it, bool columns, int kind)
    {
        if (kind == 0 && IsDef(it.MinC)) return it.MinC;
        if (kind == 1 && IsDef(it.MaxC)) return it.MaxC;
        if (kind == 2 && IsDef(it.Minimum)) return it.Minimum;
        var n = it.Node;
        var cs = n.Style;
        float v;
        if (columns)
        {
            var m = ResolveMargin(cs, float.NaN, out _);
            if (kind == 2) v = MinimumWidth(ref it, n, cs) + m.Horizontal;
            else
                v = MeasureNode(n, float.NaN, float.NaN, float.NaN, float.NaN, kind == 0 ? AvailableSpace.MinContent : AvailableSpace.MaxContent,
                    AvailableSpace.MaxContent, SizingMode.Inherent, widthOnly: true).Width + m.Horizontal;
        }
        else
        {
            EnsureItemWidth(ref it);
            if (kind == 2) v = MinimumHeight(ref it, n, cs) + it.MT + it.MB;
            else
            {
                v = MeasureNode(n, it.W, float.NaN, it.AreaW, float.NaN, AvailableSpace.Definite(it.AreaW), AvailableSpace.MaxContent,
                    SizingMode.Inherent).Height + it.MT + it.MB;
                it.MinC = it.MaxC = v;
            }
        }
        if (!(v >= 0) || float.IsInfinity(v)) v = 0;
        switch (kind)
        {
            case 0: it.MinC = v; break;
            case 1: it.MaxC = v; break;
            default: it.Minimum = v; break;
        }
        return v;
    }

    float MinimumWidth(ref GItem it, LayoutNode n, LayoutStyle cs)
    {
        var p = ResolvePadding(cs, float.NaN);
        var bo = ResolveBorder(cs);
        float pbW = p.Horizontal + bo.Horizontal, pbH = p.Vertical + bo.Vertical;
        if (!cs.MinWidth.IsAuto)
        {
            float adj = cs.BoxSizing == BoxSizing.ContentBox ? pbW : 0;
            float mv = SizeValue(n, cs.MinWidth, float.NaN, float.NaN, float.NaN, adj, true);
            return IsDef(mv) ? Math.Max(mv, pbW) : pbW;
        }
        int span = it.C1 - it.C0;
        if (cs.IsScrollContainer || !it.SpansAutoMin || (span > 1 && it.CrossesFlex)) return pbW;
        float content = MeasureNode(n, float.NaN, float.NaN, float.NaN, float.NaN, AvailableSpace.MinContent, AvailableSpace.MaxContent,
            SizingMode.Content, widthOnly: true).Width;
        ResolveSizes(n, float.NaN, float.NaN, pbW, pbH, out var isz);
        // §6.6：网格项目的基于内容的最小尺寸 = 指定尺寸建议（若有），否则内容尺寸建议；再受最大尺寸限制。
        // 只跨最大尺寸函数固定的轨道时，还钳制到这些轨道（与其间的间距）之和减去外边距（例如 minmax(auto, 100px) 不被长单词撑过 100px）
        if (IsDef(isz.Width)) content = isz.Width;
        content = Math.Min(content, isz.MaxWidth);
        if (!float.IsPositiveInfinity(it.FixedMax)) content = Math.Min(content, it.FixedMax - ResolveMargin(cs, float.NaN, out _).Horizontal);
        return Math.Max(content, pbW);
    }

    float MinimumHeight(ref GItem it, LayoutNode n, LayoutStyle cs)
    {
        var p = ResolvePadding(cs, it.AreaW);
        var bo = ResolveBorder(cs);
        float pbW = p.Horizontal + bo.Horizontal, pbH = p.Vertical + bo.Vertical;
        if (!cs.MinHeight.IsAuto)
        {
            float adj = cs.BoxSizing == BoxSizing.ContentBox ? pbH : 0;
            float mv = SizeValue(n, cs.MinHeight, float.NaN, it.AreaW, float.NaN, adj, false);
            return IsDef(mv) ? Math.Max(mv, pbH) : pbH;
        }
        int span = it.R1 - it.R0;
        if (cs.IsScrollContainer || !it.SpansAutoMin || (span > 1 && it.CrossesFlex)) return pbH;
        float content = MeasureNode(n, it.W, float.NaN, it.AreaW, float.NaN, AvailableSpace.Definite(it.AreaW), AvailableSpace.MaxContent,
            SizingMode.Content).Height;
        ResolveSizes(n, it.AreaW, float.NaN, pbW, pbH, out var isz);
        if (IsDef(isz.Height)) content = isz.Height;
        content = Math.Min(content, isz.MaxHeight);
        if (!float.IsPositiveInfinity(it.FixedMax)) content = Math.Min(content, it.FixedMax - it.MT - it.MB);
        return Math.Max(content, pbH);
    }

    /// <summary>项目在宽 <see cref="GItem.AreaW"/> 的区域中使用的边框盒宽度（stretch 或 fit-content）与上下外边距。</summary>
    void EnsureItemWidth(ref GItem it)
    {
        if (it.WidthFor == it.AreaW) return;
        float aw = it.AreaW;
        var n = it.Node;
        var cs = n.Style;
        var cm = ResolveMargin(cs, aw, out int autos);
        var cp = ResolvePadding(cs, aw);
        var cb = ResolveBorder(cs);
        ResolveSizes(n, aw, float.NaN, cp.Horizontal + cb.Horizontal, cp.Vertical + cb.Vertical, out var csz);
        float cw = csz.ClampedWidth;
        if (!IsDef(cw))
        {
            if (it.JAlign == ItemStretch && cs.Width.IsAuto && (autos & (AutoLeft | AutoRight)) == 0)
                cw = Clamp(aw - cm.Horizontal, csz.MinWidth, csz.MaxWidth);
            else
                cw = MeasureNode(n, float.NaN, float.NaN, aw, float.NaN, AvailableSpace.Definite(aw), AvailableSpace.MaxContent,
                    SizingMode.Inherent, widthOnly: true).Width;
        }
        it.W = cw;
        it.MT = cm.Top;
        it.MB = cm.Bottom;
        it.WidthFor = aw;
    }

    /// <summary>§11.5.1 把一个项目的贡献超出所跨轨道当前尺寸的部分分配到受影响轨道的「计划增量」中。</summary>
    void DistributeExtra(GTrack[] t, int a, int b, float contribution, int phase, byte constraint, bool flexMode, float gap)
    {
        bool baseP = phase <= 3;
        float used = 0;
        int visible = 0;
        for (int k = a; k < b; k++)
        {
            if (!t[k].Collapsed) visible++;
            used += baseP || float.IsPositiveInfinity(t[k].Limit) ? t[k].Base : t[k].Limit;
        }
        used += gap * Math.Max(0, visible - 1);
        float space = contribution - used;
        if (!(space > 0)) return;

        EnsureScratch(b - a);
        var idx = _gIdx;
        var cap = _gCap;
        var weight = _gWeight;
        int m = 0;
        float flexSum = 0;
        for (int k = a; k < b; k++)
        {
            ref var tr = ref t[k];
            if (!Affected(ref tr, phase, constraint, flexMode)) continue;
            tr.Inc = 0;
            float size = baseP || float.IsPositiveInfinity(tr.Limit) ? tr.Base : tr.Limit;
            float lim;
            if (baseP) lim = tr.MaxK == TkFit ? Math.Min(tr.Limit, tr.MaxV) : tr.Limit;
            else lim = !float.IsPositiveInfinity(tr.Limit) && !tr.InfGrow ? tr.Limit : tr.MaxK == TkFit ? tr.MaxV : float.PositiveInfinity;
            idx[m] = k;
            cap[m] = Math.Max(0, lim - size);
            weight[m] = flexMode ? tr.Flex : 1;
            flexSum += weight[m];
            m++;
        }
        if (m == 0) return;
        if (flexMode && !(flexSum > 0))
            for (int q = 0; q < m; q++) weight[q] = 1;

        space = WaterFill(t, idx, cap, weight, m, space);
        if (space > 0.0001f)
        {
            // 超出上限：分给（相应地）有固有最大尺寸函数的轨道，没有则分给全部受影响轨道
            int chosen = 0;
            float wsum = 0;
            for (int q = 0; q < m; q++)
            {
                ref var tr = ref t[idx[q]];
                if (BeyondLimitTarget(ref tr, phase, baseP)) { chosen++; wsum += weight[q]; }
            }
            bool all = chosen == 0 || flexMode;
            if (all)
            {
                wsum = 0;
                for (int q = 0; q < m; q++) wsum += weight[q];
            }
            if (wsum > 0)
                for (int q = 0; q < m; q++)
                {
                    ref var tr = ref t[idx[q]];
                    if (all || BeyondLimitTarget(ref tr, phase, baseP)) tr.Inc += space * (weight[q] / wsum);
                }
        }
        for (int q = 0; q < m; q++)
        {
            ref var tr = ref t[idx[q]];
            if (tr.Inc > tr.Planned) tr.Planned = tr.Inc;
            if (!tr.Touched)
            {
                tr.Touched = true;
                _gTouched.Add(idx[q]);
            }
        }
    }

    static bool BeyondLimitTarget(ref GTrack tr, int phase, bool baseP)
    {
        float size = baseP || float.IsPositiveInfinity(tr.Limit) ? tr.Base : tr.Limit;
        bool fitOpen = tr.MaxK == TkFit && size + tr.Inc < tr.MaxV;
        return phase switch
        {
            1 or 2 => tr.MaxK is TkAuto or TkMinC or TkMaxC || fitOpen,
            3 => tr.MaxK is TkAuto or TkMaxC || fitOpen,
            _ => tr.MaxK != TkFit || fitOpen,
        };
    }

    static bool Affected(ref GTrack tr, int phase, byte constraint, bool flexMode)
    {
        if (tr.Collapsed) return false;
        if (flexMode && tr.MaxK != TkFlex) return false;
        return phase switch
        {
            1 => tr.MinK is TkAuto or TkMinC or TkMaxC,
            2 => tr.MinK is TkMinC or TkMaxC,
            3 => constraint == CMax ? tr.MinK is TkAuto or TkMaxC : tr.MinK == TkMaxC,
            4 => tr.MaxK is TkAuto or TkMinC or TkMaxC or TkFit,
            _ => tr.MaxK is TkAuto or TkMaxC or TkFit,
        };
    }

    /// <summary>按权重均分 <paramref name="space"/>，每条轨道不超过其容量（注水法：按「容量 / 权重」升序处理）。返回剩余空间。</summary>
    float WaterFill(GTrack[] t, int[] idx, float[] cap, float[] weight, int m, float space)
    {
        var keys = _gKeys;
        var order = _gOrd;
        for (int q = 0; q < m; q++)
        {
            order[q] = q;
            keys[q] = weight[q] > 0 ? cap[q] / weight[q] : float.PositiveInfinity;
        }
        Array.Sort(keys, order, 0, m);
        float wRem = 0;
        for (int q = 0; q < m; q++) wRem += weight[q];
        for (int q = 0; q < m; q++)
        {
            int j = order[q];
            float w = weight[j];
            if (!(wRem > 0) || !(space > 0)) break;
            float share = space * (w / wRem);
            ref var tr = ref t[idx[j]];
            if (cap[j] <= share)
            {
                tr.Inc += cap[j];
                space -= cap[j];
            }
            else
            {
                // 其余轨道的容量都不小于这一份：按权重平分剩余空间
                for (int r = q; r < m; r++)
                {
                    int jr = order[r];
                    t[idx[jr]].Inc += space * (weight[jr] / wRem);
                }
                return 0;
            }
            wRem -= w;
        }
        return Math.Max(0, space);
    }

    void ApplyPlanned(GTrack[] t, int phase)
    {
        bool baseP = phase <= 3;
        foreach (int k in _gTouched)
        {
            ref var tr = ref t[k];
            if (baseP) tr.Base += tr.Planned;
            else if (float.IsPositiveInfinity(tr.Limit))
            {
                tr.Limit = tr.Base + tr.Planned;
                if (phase == 4) tr.InfGrow = true;
            }
            else tr.Limit += tr.Planned;
            tr.Planned = 0;
            tr.Touched = false;
            if (tr.Limit < tr.Base) tr.Limit = tr.Base;
        }
        _gTouched.Clear();
    }

    /// <summary>§11.6：把正的自由空间均分给各轨道的基准尺寸，直到增长上限。</summary>
    void GrowToLimits(GTrack[] t, int n, float free)
    {
        for (int i = 0; i < n; i++) free -= t[i].Base;
        if (!(free > 0)) return;
        EnsureScratch(n);
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (t[i].Collapsed) continue;
            t[i].Inc = 0;
            _gIdx[m] = i;
            _gCap[m] = Math.Max(0, t[i].Limit - t[i].Base);
            _gWeight[m] = 1;
            m++;
        }
        WaterFill(t, _gIdx, _gCap, _gWeight, m, free);
        for (int q = 0; q < m; q++) t[_gIdx[q]].Base += t[_gIdx[q]].Inc;
    }

    /// <summary>§11.7.1 求 1fr 的尺寸：轨道 [a, b) 填满 <paramref name="space"/>，基准尺寸大于按比例份额的 fr 轨道按不可伸缩处理。</summary>
    static float FindFrSize(GTrack[] t, int a, int b, float space, float gap)
    {
        int visible = 0;
        for (int k = a; k < b; k++)
        {
            t[k].Inflexible = false;
            if (!t[k].Collapsed) visible++;
        }
        float gaps = gap * Math.Max(0, visible - 1);
        for (int guard = 0; guard <= b - a; guard++)
        {
            float leftover = space - gaps, flexSum = 0;
            for (int k = a; k < b; k++)
            {
                if (t[k].MaxK == TkFlex && !t[k].Inflexible) flexSum += t[k].Flex;
                else leftover -= t[k].Base;
            }
            if (flexSum < 1) flexSum = 1;
            float hyp = leftover / flexSum;
            bool changed = false;
            for (int k = a; k < b; k++)
            {
                if (t[k].MaxK != TkFlex || t[k].Inflexible) continue;
                if (hyp * t[k].Flex < t[k].Base)
                {
                    t[k].Inflexible = true;
                    changed = true;
                }
            }
            if (!changed) return hyp;
        }
        return 0;
    }

    /// <summary>按 content-distribution 求各轨道的起点（相对内容盒的逻辑起始边）。</summary>
    static void PositionTracks(GTrack[] t, int n, float inner, float gap, byte dist)
    {
        int visible = CountVisible(t, n);
        float total = gap * Math.Max(0, visible - 1);
        for (int i = 0; i < n; i++) total += t[i].Base;
        float lead = 0, between = 0;
        if (IsDef(inner)) Distribute(dist, inner - total, visible, out lead, out between);
        float pos = lead;
        int seen = 0;
        for (int i = 0; i < n; i++)
        {
            t[i].Pos = pos;
            if (t[i].Collapsed) continue;
            pos += t[i].Base;
            if (++seen < visible) pos += gap + between;
        }
    }

    // ======================================================================
    // 项目在区域中的尺寸与位置
    // ======================================================================

    LayoutOutput LayoutGridItem(ref GItem it, GTrack[] cols, GTrack[] rows, float innerW, float contentLeft, float contentTop, bool rtl,
        RunMode mode, bool baseline)
    {
        var n = it.Node;
        var cs = n.Style;
        float ax = cols[it.C0].Pos, aw = Math.Max(0, cols[it.C1 - 1].End - ax);
        float ay = rows[it.R0].Pos, ah = Math.Max(0, rows[it.R1 - 1].End - ay);
        var cm = ResolveMargin(cs, aw, out int autos);
        var cp = ResolvePadding(cs, aw);
        var cb = ResolveBorder(cs);
        ResolveSizes(n, aw, ah, cp.Horizontal + cb.Horizontal, cp.Vertical + cb.Vertical, out var csz);

        // 逻辑方向的外边距（rtl 时行内起始侧是物理右侧）
        float ms = rtl ? cm.Right : cm.Left, me = rtl ? cm.Left : cm.Right;
        bool autoS = (autos & (rtl ? AutoRight : AutoLeft)) != 0, autoE = (autos & (rtl ? AutoLeft : AutoRight)) != 0;
        bool autoT = (autos & AutoTop) != 0, autoB = (autos & AutoBottom) != 0;

        float cw = csz.ClampedWidth, ch = csz.ClampedHeight;
        if (!IsDef(cw) && it.JAlign == ItemStretch && cs.Width.IsAuto && !autoS && !autoE)
            cw = Clamp(aw - cm.Horizontal, csz.MinWidth, csz.MaxWidth);
        if (!IsDef(ch) && it.AAlign == ItemStretch && cs.Height.IsAuto && !autoT && !autoB)
            ch = Clamp(ah - cm.Vertical, csz.MinHeight, csz.MaxHeight);

        var ci = new LayoutInput
        {
            KnownWidth = cw,
            KnownHeight = ch,
            ParentWidth = aw,
            ParentHeight = ah,
            AvailableWidth = AvailableSpace.Definite(aw),
            AvailableHeight = AvailableSpace.Definite(ah),
            Mode = mode,
            Sizing = SizingMode.Inherent,
            RequestBaseline = baseline,
        };
        var o = ComputeNode(n, ci);

        float freeX = aw - o.Width - ms - me;
        float offX = 0;
        if (autoS || autoE)
        {
            if (freeX > 0)
            {
                if (autoS && autoE) { ms += freeX / 2; me += freeX / 2; }
                else if (autoS) ms += freeX;
                else me += freeX;
            }
        }
        else offX = it.JAlign switch { ItemEnd => freeX, ItemCenter => freeX / 2, _ => 0 };
        float freeY = ah - o.Height - cm.Top - cm.Bottom;
        float mt = cm.Top, mb = cm.Bottom, offY = 0;
        if (autoT || autoB)
        {
            if (freeY > 0)
            {
                if (autoT && autoB) { mt += freeY / 2; mb += freeY / 2; }
                else if (autoT) mt += freeY;
                else mb += freeY;
            }
        }
        else offY = it.AAlign switch { ItemEnd => freeY, ItemCenter => freeY / 2, _ => 0 };

        float logicalX = ax + ms + offX;
        float x = rtl ? contentLeft + innerW - logicalX - o.Width : contentLeft + logicalX;
        float y = contentTop + ay + mt + offY;
        _gLastItemY = y;
        if (mode == RunMode.PerformLayout)
        {
            RelativeOffset(cs, aw, ah, rtl, out float dx, out float dy);
            n._x = x + dx;
            n._y = y + dy;
            n._margin = rtl ? new BoxEdges(me, mt, ms, mb) : new BoxEdges(ms, mt, me, mb);
        }
        return o;
    }

    /// <summary>
    /// 绝对定位的子节点给出了网格放置属性时，以对应的网格区域作为包含块（§9.1）：auto 的一侧取内边距边；
    /// 指向不存在的线（超出隐式网格）按 auto。区域写入子节点（网格容器边框盒坐标）。
    /// </summary>
    void SetGridContainingBlock(LayoutNode c, LayoutNode grid, GridAxis colAx, GridAxis rowAx, GTrack[] cols, int nCols,
        GTrack[] rows, int nRows, float w, float h, in BoxEdges pad, in BoxEdges bor, bool rtl)
    {
        var cs = c.Style;
        if (cs.GridColumnStart.IsAuto && cs.GridColumnEnd.IsAuto && cs.GridRowStart.IsAuto && cs.GridRowEnd.IsAuto)
        {
            c._gridCbOwner = null;
            return;
        }
        float innerW = Math.Max(0, w - pad.Horizontal - bor.Horizontal), innerH = Math.Max(0, h - pad.Vertical - bor.Vertical);
        AbsArea(cs.GridColumnStart, cs.GridColumnEnd, colAx, cols, nCols, innerW, rtl ? pad.Right : pad.Left, rtl ? pad.Left : pad.Right,
            out float x0, out float x1);
        AbsArea(cs.GridRowStart, cs.GridRowEnd, rowAx, rows, nRows, innerH, pad.Top, pad.Bottom, out float y0, out float y1);
        float contentLeft = bor.Left + pad.Left;
        c._gridCbOwner = grid;
        c._gridCbX = rtl ? contentLeft + innerW - x1 : contentLeft + x0;
        c._gridCbW = Math.Max(0, x1 - x0);
        c._gridCbY = bor.Top + pad.Top + y0;
        c._gridCbH = Math.Max(0, y1 - y0);
    }

    void AbsArea(GridLine start, GridLine end, GridAxis ax, GTrack[] t, int n, float inner, float padStart, float padEnd,
        out float p0, out float p1)
    {
        int a = int.MinValue, b = int.MinValue;
        bool sLine = start.Kind == GridLineKind.Line, eLine = end.Kind == GridLineKind.Line;
        if (sLine) a = ResolveLine(start, true, ax) - ax.Min;
        if (eLine) b = ResolveLine(end, false, ax) - ax.Min;
        if (sLine && !eLine && end.IsSpan) b = SpanForward(a + ax.Min, end, ax) - ax.Min;
        if (eLine && !sLine && start.IsSpan) a = SpanBackward(b + ax.Min, start, ax) - ax.Min;
        if (a != int.MinValue && b != int.MinValue && a > b) (a, b) = (b, a);
        if (a < 0 || a > n) a = int.MinValue;
        if (b < 0 || b > n) b = int.MinValue;
        p0 = a == int.MinValue ? -padStart : a == n ? t[n - 1].End : t[a].Pos;
        p1 = b == int.MinValue ? inner + padEnd : b == 0 ? t[0].Pos : t[b - 1].End;
        if (p1 < p0) p1 = p0;
    }
}
