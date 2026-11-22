using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Layout;
using CssLength = AwAUI.Css.Values.LengthPercentage;
using LayoutLength = AwAUI.Layout.LengthPercentage;

namespace AwAUI.UI;

/// <summary>
/// 含百分比的 CSS 长度（<c>calc(50% - 8px)</c>、<c>min(100%, 400px)</c> ...）在布局层的表示（design/08）。
/// 按 CSS 计算值做<b>值相等</b>比较：每次样式重算都会产生新实例，若按引用比较，<see cref="LayoutStyle"/> 会被误判为变化而触发重排。
/// <see cref="Resolve"/> 是纯函数。
/// </summary>
internal sealed class CssLayoutCalc : LayoutCalc
{
    readonly CssLength _value;

    public CssLayoutCalc(CssLength value) => _value = value;

    public override float Resolve(float percentBasis)
    {
        if (float.IsNaN(percentBasis)) return float.NaN;
        return _value.Resolve(percentBasis);
    }

    public override bool HasPercentage => _value.HasPercentage || _value.Calc is not null;

    public override bool Equals(object? obj) => obj is CssLayoutCalc c && c._value.Equals(_value);

    public override int GetHashCode() => _value.GetHashCode();

    public override string ToString() => _value.ToString();
}

/// <summary>
/// 计算样式 → 布局输入（design/07「ComputedStyle → LayoutStyle 映射」）。只在布局类分组变化时调用（结果按计算样式缓存在
/// <see cref="RenderBox.MappedStyle"/>），映射结果相等时 <see cref="LayoutNode.Style"/> 不会标脏。
/// <list type="bullet">
/// <item>CSS 的 <c>display</c> 初始值是 <c>inline</c>：没有行内格式化上下文（design/08），<c>inline</c> / <c>inline-block</c> 映射为
/// 收缩适应宽度的 <see cref="Display.InlineFlex"/>（<c>inline-block</c> 用纵向主轴，子元素像块一样堆叠）；<c>grid / inline-grid</c> 映射为网格。</item>
/// <item>网格的轨道列表、区域与放置线按内容转换为布局类型（按计算值实例缓存）；<c>order</c>、<c>justify-self</c>、网格放置属性对所有元素映射。</item>
/// <item>含百分比的 padding / gap 由布局引擎在解析后截断为非负；宽高等尺寸由引擎按 min/max 约束处理。</item>
/// <item><c>position: sticky</c> 映射为 <see cref="Position.Sticky"/>：布局时与 static 相同（占据静态空间），
/// 滚动偏移产生的视觉偏移由 <see cref="RenderPipeline"/> 在绝对几何阶段施加（design/08）。</item>
/// </list>
/// </summary>
internal static class LayoutMapper
{
    /// <summary>匿名文本叶子（容器中的裸字符串）使用的样式：块级、无盒模型装饰。</summary>
    public static readonly LayoutStyle AnonymousText = LayoutStyle.Default;

    /// <param name="scale">设备像素比：边框宽度与绘制一样向下对齐到设备像素（非零时至少 1 个设备像素），布局与绘制的边框一致。</param>
    public static LayoutStyle Map(ComputedStyle s, float scale)
    {
        var display = MapDisplay(s.Display, out bool inlineBlock);
        var style = new LayoutStyle
        {
            Display = display,
            // direction 影响块级对齐、flex 主轴与网格列方向（见 LayoutEngine）
            Direction = s.Direction == CssDirection.Rtl ? Direction.Rtl : Direction.Ltr,
            Position = s.Position switch
            {
                CssPosition.Relative => Position.Relative,
                CssPosition.Sticky => Position.Sticky,
                CssPosition.Absolute => Position.Absolute,
                CssPosition.Fixed => Position.Fixed,
                _ => Position.Static,
            },
            BoxSizing = s.BoxSizing == CssBoxSizing.BorderBox ? BoxSizing.BorderBox : BoxSizing.ContentBox,
            OverflowX = MapOverflow(s.OverflowX),
            OverflowY = MapOverflow(s.OverflowY),
            Width = ToDimension(s.Width),
            Height = ToDimension(s.Height),
            MinWidth = ToDimension(s.MinWidth),
            MinHeight = ToDimension(s.MinHeight),
            MaxWidth = ToDimension(s.MaxWidth),
            MaxHeight = ToDimension(s.MaxHeight),
            // aspect-ratio：非替换元素没有固有宽高比（替换元素由 RenderPipeline.SyncReplaced 与固有宽高比合成后覆盖）
            AspectRatio = s.AspectRatio.Resolve(null),
            Margin = new Edges<LengthPercentageAuto>(ToAuto(s.MarginTop), ToAuto(s.MarginRight), ToAuto(s.MarginBottom), ToAuto(s.MarginLeft)),
            Padding = new Edges<LayoutLength>(ToLength(s.PaddingTop), ToLength(s.PaddingRight), ToLength(s.PaddingBottom), ToLength(s.PaddingLeft)),
            Border = new Edges<float>(BorderWidth(s.BorderTopWidth, s.BorderTopStyle, scale), BorderWidth(s.BorderRightWidth, s.BorderRightStyle, scale),
                BorderWidth(s.BorderBottomWidth, s.BorderBottomStyle, scale), BorderWidth(s.BorderLeftWidth, s.BorderLeftStyle, scale)),
            Inset = new Edges<LengthPercentageAuto>(ToAuto(s.Top), ToAuto(s.Right), ToAuto(s.Bottom), ToAuto(s.Left)),
            FlexGrow = s.FlexGrow,
            FlexShrink = s.FlexShrink,
            FlexBasis = ToDimension(s.FlexBasis),
            AlignSelf = MapAlignSelf(s.AlignSelf),
            Order = s.Order,
            JustifySelf = MapJustifySelf(s.JustifySelf),
            GridRowStart = ToLine(s.GridRowStart),
            GridRowEnd = ToLine(s.GridRowEnd),
            GridColumnStart = ToLine(s.GridColumnStart),
            GridColumnEnd = ToLine(s.GridColumnEnd),
        };
        if (inlineBlock)
        {
            // inline-block：子元素按块堆叠（纵向主轴、拉伸），flex 容器属性不生效
            return style with { FlexDirection = FlexDirection.Column };
        }
        if (s.Display == CssDisplay.TableRow)
        {
            // 表格行映射为横向 flex：单元格默认拉伸到行高（与表格单元格一样等高）
            return style with { FlexDirection = FlexDirection.Row, AlignItems = AlignItems.Stretch };
        }
        if (display is Display.Flex or Display.InlineFlex)
        {
            style = style with
            {
                FlexDirection = s.FlexDirection switch
                {
                    CssFlexDirection.RowReverse => FlexDirection.RowReverse,
                    CssFlexDirection.Column => FlexDirection.Column,
                    CssFlexDirection.ColumnReverse => FlexDirection.ColumnReverse,
                    _ => FlexDirection.Row,
                },
                FlexWrap = s.FlexWrap switch
                {
                    CssFlexWrap.Wrap => FlexWrap.Wrap,
                    CssFlexWrap.WrapReverse => FlexWrap.WrapReverse,
                    _ => FlexWrap.NoWrap,
                },
                JustifyContent = MapJustify(s.JustifyContent),
                AlignItems = MapAlignItems(s.AlignItems),
                AlignContent = MapAlignContent(s.AlignContent),
                RowGap = ToGap(s.RowGap),
                ColumnGap = ToGap(s.ColumnGap),
            };
        }
        else if (display is Display.Grid or Display.InlineGrid)
        {
            style = style with
            {
                GridTemplateColumns = ToTrackList(s.GridTemplateColumns),
                GridTemplateRows = ToTrackList(s.GridTemplateRows),
                GridTemplateAreas = ToAreas(s.GridTemplateAreas),
                GridAutoColumns = ToAutoTracks(s.GridAutoColumns),
                GridAutoRows = ToAutoTracks(s.GridAutoRows),
                GridAutoFlow = (GridAutoFlow)(int)s.GridAutoFlow,
                JustifyItems = MapJustifyItems(s.JustifyItems),
                JustifyContent = MapJustify(s.JustifyContent),
                AlignItems = MapAlignItems(s.AlignItems),
                AlignContent = MapAlignContent(s.AlignContent),
                RowGap = ToGap(s.RowGap),
                ColumnGap = ToGap(s.ColumnGap),
            };
        }
        return style;
    }

    /// <summary>
    /// 子元素的绘制顺序（CSS Display 3「order-modified document order」）：flex / grid 容器的子元素按 (<c>order</c>, 文档顺序) 稳定排序，
    /// 与布局引擎排列项目的顺序一致；其他容器、或所有子元素的 order 都是 0 时直接返回 <see cref="Element.Children"/>（不分配）。
    /// 命中测试按绘制记录的逆序进行，所以随之一致。<paramref name="scratch"/> 是调用方复用的缓冲。
    /// </summary>
    internal static IReadOnlyList<Element> PaintOrder(Element container, List<Element> scratch)
    {
        // ::before / ::after 盒（帧管线私有的元素，不在子节点列表中）：第一个 / 最后一个绘制
        var (before, after) = container.Render?.Kind == BoxKind.Container ? RenderPipeline.GeneratedOf(container) : (null, null);
        if (before is null && after is null) return OrderedChildren(container, scratch);
        var ordered = OrderedChildren(container, scratch);
        var list = new List<Element>(ordered.Count + 2);
        if (before is not null) list.Add(before);
        list.AddRange(ordered);
        if (after is not null) list.Add(after);
        scratch.Clear();
        scratch.AddRange(list);
        return scratch;
    }

    static IReadOnlyList<Element> OrderedChildren(Element container, List<Element> scratch)
    {
        var ch = container.Children;
        var display = container.Render?.Style?.Display;
        if (display is not (CssDisplay.Flex or CssDisplay.InlineFlex or CssDisplay.Grid or CssDisplay.InlineGrid)) return ch;
        bool any = false;
        for (int i = 0; i < ch.Count && !any; i++) any = ch[i].Render?.Style is { Order: not 0 };
        if (!any) return ch;
        scratch.Clear();
        for (int i = 0; i < ch.Count; i++) scratch.Add(ch[i]);
        if (scratch.Count > 32)
        {
            // 键 = (order, 原下标)，互不相同，所以结果稳定；O(n log n)
            var keys = new long[scratch.Count];
            var arr = scratch.ToArray();
            for (int i = 0; i < arr.Length; i++) keys[i] = ((long)OrderOf(arr[i]) << 32) | (uint)i;
            Array.Sort(keys, arr);
            for (int i = 0; i < arr.Length; i++) scratch[i] = arr[i];
            return scratch;
        }
        // 插入排序：稳定，子元素少时最快
        for (int i = 1; i < scratch.Count; i++)
        {
            var x = scratch[i];
            int o = OrderOf(x), j = i - 1;
            while (j >= 0 && OrderOf(scratch[j]) > o)
            {
                scratch[j + 1] = scratch[j];
                j--;
            }
            scratch[j + 1] = x;
        }
        return scratch;

        static int OrderOf(Element e) => e.Render?.Style?.Order ?? 0;
    }

    /// <summary>
    /// 替换元素（图片、SVG）的布局样式：标记为替换元素（块布局与绝对定位都不拉伸其 auto 尺寸，网格中 normal 按 start），
    /// 并用合成后的宽高比（<paramref name="usedRatio"/>，CSS <c>aspect-ratio</c> 与固有比例合成）覆盖 CSS 的比值。
    /// 由 <see cref="RenderPipeline"/> 在映射替换元素时调用，取代原来「display 映射为 InlineFlex」的规避做法。
    /// </summary>
    public static LayoutStyle ForReplaced(LayoutStyle mapped, float? usedRatio) =>
        mapped with { IsReplaced = true, AspectRatio = usedRatio ?? mapped.AspectRatio };

    /// <summary>与 <see cref="BoxPainter.GetBorderWidths"/> 相同的使用值：none / hidden 为 0，其余对齐到设备像素。</summary>
    static float BorderWidth(float width, CssLineStyle style, float scale) =>
        style is CssLineStyle.None or CssLineStyle.Hidden ? 0 : BoxPainter.SnapWidth(width, scale);

    static Display MapDisplay(CssDisplay d, out bool inlineBlock)
    {
        inlineBlock = false;
        switch (d)
        {
            case CssDisplay.Block:
            case CssDisplay.FlowRoot:
            // 没有表格与列表标记的布局：表格、行组、单元格、标题与 list-item 按块处理；-webkit-box 只用于行数截断（见 TextStyleMapper）
            case CssDisplay.Table:
            case CssDisplay.TableRowGroup:
            case CssDisplay.TableHeaderGroup:
            case CssDisplay.TableFooterGroup:
            case CssDisplay.TableCell:
            case CssDisplay.TableCaption:
            case CssDisplay.ListItem:
            case CssDisplay.WebkitBox:
                return Display.Block;
            case CssDisplay.TableRow:
                // 表格行：单元格横向排列（近似；列宽不跨行对齐）
                return Display.Flex;
            case CssDisplay.TableColumn:
            case CssDisplay.TableColumnGroup:
                // 列与列组不生成承载内容的盒
                return Display.None;
            case CssDisplay.Flex:
                return Display.Flex;
            case CssDisplay.Grid:
                return Display.Grid;
            case CssDisplay.InlineFlex:
                return Display.InlineFlex;
            case CssDisplay.InlineGrid:
                return Display.InlineGrid;
            case CssDisplay.None:
                return Display.None;
            case CssDisplay.Contents:
                return Display.Contents;
            default:
                // inline、inline-block、inline-table、-webkit-inline-box：没有行内格式化上下文，按收缩适应的块处理
                inlineBlock = true;
                return Display.InlineFlex;
        }
    }

    static Overflow MapOverflow(CssOverflow o) => o switch
    {
        CssOverflow.Hidden => Overflow.Hidden,
        CssOverflow.Clip => Overflow.Clip,
        CssOverflow.Scroll => Overflow.Scroll,
        CssOverflow.Auto => Overflow.Auto,
        _ => Overflow.Visible,
    };

    static JustifyContent MapJustify(CssJustifyContent j) => j switch
    {
        CssJustifyContent.FlexStart => JustifyContent.FlexStart,
        CssJustifyContent.FlexEnd => JustifyContent.FlexEnd,
        CssJustifyContent.Start or CssJustifyContent.Left => JustifyContent.Start,
        CssJustifyContent.End or CssJustifyContent.Right => JustifyContent.End,
        CssJustifyContent.Center => JustifyContent.Center,
        CssJustifyContent.SpaceBetween => JustifyContent.SpaceBetween,
        CssJustifyContent.SpaceAround => JustifyContent.SpaceAround,
        CssJustifyContent.SpaceEvenly => JustifyContent.SpaceEvenly,
        CssJustifyContent.Stretch => JustifyContent.Stretch,
        _ => JustifyContent.Normal,
    };

    static AlignItems MapAlignItems(CssAlignItems a) => a switch
    {
        CssAlignItems.Stretch => AlignItems.Stretch,
        CssAlignItems.FlexStart => AlignItems.FlexStart,
        CssAlignItems.FlexEnd => AlignItems.FlexEnd,
        CssAlignItems.Start or CssAlignItems.SelfStart => AlignItems.Start,
        CssAlignItems.End or CssAlignItems.SelfEnd => AlignItems.End,
        CssAlignItems.Center => AlignItems.Center,
        CssAlignItems.Baseline => AlignItems.Baseline,
        _ => AlignItems.Normal,
    };

    static AlignSelf MapAlignSelf(CssAlignSelf a) => a switch
    {
        CssAlignSelf.Normal => AlignSelf.Normal,
        CssAlignSelf.Stretch => AlignSelf.Stretch,
        CssAlignSelf.FlexStart => AlignSelf.FlexStart,
        CssAlignSelf.FlexEnd => AlignSelf.FlexEnd,
        CssAlignSelf.Start or CssAlignSelf.SelfStart => AlignSelf.Start,
        CssAlignSelf.End or CssAlignSelf.SelfEnd => AlignSelf.End,
        CssAlignSelf.Center => AlignSelf.Center,
        CssAlignSelf.Baseline => AlignSelf.Baseline,
        _ => AlignSelf.Auto,
    };

    static JustifyItems MapJustifyItems(CssJustifyItems j) => j switch
    {
        CssJustifyItems.Stretch => JustifyItems.Stretch,
        CssJustifyItems.Start => JustifyItems.Start,
        CssJustifyItems.End => JustifyItems.End,
        CssJustifyItems.Center => JustifyItems.Center,
        CssJustifyItems.Left => JustifyItems.Left,
        CssJustifyItems.Right => JustifyItems.Right,
        CssJustifyItems.FlexStart => JustifyItems.FlexStart,
        CssJustifyItems.FlexEnd => JustifyItems.FlexEnd,
        CssJustifyItems.SelfStart => JustifyItems.SelfStart,
        CssJustifyItems.SelfEnd => JustifyItems.SelfEnd,
        CssJustifyItems.Baseline => JustifyItems.Baseline,
        // legacy（初始值）只对块容器有意义；网格中按 normal
        _ => JustifyItems.Normal,
    };

    static JustifySelf MapJustifySelf(CssJustifySelf j) => j switch
    {
        CssJustifySelf.Normal => JustifySelf.Normal,
        CssJustifySelf.Stretch => JustifySelf.Stretch,
        CssJustifySelf.Start => JustifySelf.Start,
        CssJustifySelf.End => JustifySelf.End,
        CssJustifySelf.Center => JustifySelf.Center,
        CssJustifySelf.Left => JustifySelf.Left,
        CssJustifySelf.Right => JustifySelf.Right,
        CssJustifySelf.FlexStart => JustifySelf.FlexStart,
        CssJustifySelf.FlexEnd => JustifySelf.FlexEnd,
        CssJustifySelf.SelfStart => JustifySelf.SelfStart,
        CssJustifySelf.SelfEnd => JustifySelf.SelfEnd,
        CssJustifySelf.Baseline => JustifySelf.Baseline,
        _ => JustifySelf.Auto,
    };

    // ---------------- 网格值的转换（按计算值实例缓存：同一条规则的计算值实例在元素间共享） ----------------

    static readonly ConditionalWeakTable<CssGridTrackList, GridTrackList> s_trackLists = new();
    static readonly ConditionalWeakTable<CssGridTemplateAreas, GridAreas> s_areas = new();

    internal static GridTrackList? ToTrackList(CssGridTrackList v) => v.IsNone ? null : s_trackLists.GetValue(v, BuildTrackList);

    static GridTrackList BuildTrackList(CssGridTrackList v)
    {
        var segments = new List<GridTrackRepeat>();
        var tracks = new List<GridTrackSize>();
        var names = new List<List<string>> { new() };

        void Flush()
        {
            if (tracks.Count > 0 || names[0].Count > 0)
                segments.Add(new GridTrackRepeat(GridRepeatKind.Count, 1, tracks.ToArray(), names.ConvertAll(n => (IReadOnlyList<string>)n.ToArray())));
            tracks.Clear();
            names.Clear();
            names.Add(new List<string>());
        }

        foreach (var c in v.Components)
        {
            switch (c.Kind)
            {
                case CssGridComponentKind.LineNames:
                    names[names.Count - 1].AddRange(c.Names);
                    break;
                case CssGridComponentKind.Track:
                    tracks.Add(ToTrackSize(c.Track));
                    names.Add(new List<string>());
                    break;
                default:
                {
                    Flush();
                    var rep = c.Repeat!;
                    var rTracks = new List<GridTrackSize>();
                    var rNames = new List<List<string>> { new() };
                    foreach (var rc in rep.Components)
                    {
                        if (rc.Kind == CssGridComponentKind.LineNames) rNames[rNames.Count - 1].AddRange(rc.Names);
                        else
                        {
                            rTracks.Add(ToTrackSize(rc.Track));
                            rNames.Add(new List<string>());
                        }
                    }
                    var kind = rep.Kind switch
                    {
                        CssGridRepeatKind.AutoFill => GridRepeatKind.AutoFill,
                        CssGridRepeatKind.AutoFit => GridRepeatKind.AutoFit,
                        _ => GridRepeatKind.Count,
                    };
                    segments.Add(new GridTrackRepeat(kind, rep.Count, rTracks, rNames.ConvertAll(n => (IReadOnlyList<string>)n.ToArray())));
                    break;
                }
            }
        }
        Flush();
        return new GridTrackList(segments);
    }

    /// <summary><c>grid-auto-*</c>：只有一条 auto 轨道时为 null（引擎的默认值）。</summary>
    static GridTrackList? ToAutoTracks(CssGridTrackList v)
    {
        if (v.Components.Count == 1 && v.Components[0].Kind == CssGridComponentKind.Track && v.Components[0].Track.Equals(CssGridTrackSize.Auto)) return null;
        return ToTrackList(v);
    }

    static GridTrackSize ToTrackSize(CssGridTrackSize t) => t.Form switch
    {
        CssGridTrackForm.FitContent => GridTrackSize.FitContent(ToLength(t.Max.Length)),
        CssGridTrackForm.MinMax => GridTrackSize.MinMax(ToBreadth(t.Min), ToBreadth(t.Max)),
        _ => GridTrackSize.Of(ToBreadth(t.Min)),
    };

    static GridBreadth ToBreadth(CssGridBreadth b) => b.Kind switch
    {
        CssGridBreadthKind.Length => GridBreadth.Of(ToLength(b.Length)),
        CssGridBreadthKind.Flex => GridBreadth.Fr(b.Flex),
        CssGridBreadthKind.MinContent => GridBreadth.MinContent,
        CssGridBreadthKind.MaxContent => GridBreadth.MaxContent,
        _ => GridBreadth.Auto,
    };

    static GridAreas? ToAreas(CssGridTemplateAreas a) => a.IsNone ? null : s_areas.GetValue(a, BuildAreas);

    static GridAreas BuildAreas(CssGridTemplateAreas a)
    {
        var list = new GridArea[a.Areas.Count];
        for (int i = 0; i < list.Length; i++)
        {
            var x = a.Areas[i];
            list[i] = new GridArea(x.Name, x.RowStart, x.RowEnd, x.ColumnStart, x.ColumnEnd);
        }
        return new GridAreas(a.Rows, a.Columns, list);
    }

    static GridLine ToLine(CssGridLine l) => l.Kind switch
    {
        CssGridLineKind.Span => GridLine.Span(l.Integer, l.Name),
        CssGridLineKind.Line when l.Integer == 0 => GridLine.Named(l.Name!),
        CssGridLineKind.Line => GridLine.Line(l.Integer, l.Name),
        _ => GridLine.Auto,
    };

    static AlignContent MapAlignContent(CssAlignContent a) => a switch
    {
        CssAlignContent.FlexStart => AlignContent.FlexStart,
        CssAlignContent.FlexEnd => AlignContent.FlexEnd,
        CssAlignContent.Start => AlignContent.Start,
        CssAlignContent.End => AlignContent.End,
        CssAlignContent.Center => AlignContent.Center,
        CssAlignContent.SpaceBetween => AlignContent.SpaceBetween,
        CssAlignContent.SpaceAround => AlignContent.SpaceAround,
        CssAlignContent.SpaceEvenly => AlignContent.SpaceEvenly,
        CssAlignContent.Stretch => AlignContent.Stretch,
        _ => AlignContent.Normal,
    };

    internal static LayoutLength ToLength(CssLength v)
    {
        if (v.IsFixed) return LayoutLength.Px(v.Length);
        if (v.Calc is null && v.Length == 0) return LayoutLength.Percent(v.Percentage);
        return LayoutLength.Of(new CssLayoutCalc(v));
    }

    static Dimension ToDimension(CssLength v)
    {
        if (v.IsFixed) return Dimension.Px(v.Length);
        if (v.Calc is null && v.Length == 0) return Dimension.Percent(v.Percentage);
        return Dimension.Of(new CssLayoutCalc(v));
    }

    internal static Dimension ToDimension(SizeValue v) => v.Kind switch
    {
        SizeKind.LengthPercentage => ToDimension(v.Value),
        SizeKind.MinContent => Dimension.MinContent,
        SizeKind.MaxContent => Dimension.MaxContent,
        SizeKind.FitContent => Dimension.FitContent,
        SizeKind.Content => Dimension.Content,
        _ => Dimension.Auto,
    };

    static LengthPercentageAuto ToAuto(SizeValue v)
    {
        if (v.Kind != SizeKind.LengthPercentage) return LengthPercentageAuto.Auto;
        var lp = v.Value;
        if (lp.IsFixed) return LengthPercentageAuto.Px(lp.Length);
        if (lp.Calc is null && lp.Length == 0) return LengthPercentageAuto.Percent(lp.Percentage);
        return LengthPercentageAuto.Of(new CssLayoutCalc(lp));
    }

    /// <summary><c>gap: normal</c> 在 flex 中为 0。</summary>
    static LayoutLength ToGap(SizeValue v) => v.Kind == SizeKind.LengthPercentage ? ToLength(v.Value) : LayoutLength.Zero;

    /// <summary>
    /// 两个计算样式的布局映射是否可能不同：只看布局相关的分组引用（Box、Position、Flex、Border 的宽度）。
    /// 引用都相同时不必重新映射。
    /// </summary>
    public static bool LayoutGroupsSame(ComputedStyle a, ComputedStyle b) =>
        ReferenceEquals(a.BoxGroup, b.BoxGroup) && ReferenceEquals(a.PositionGroup, b.PositionGroup)
        && ReferenceEquals(a.FlexGroup, b.FlexGroup) && ReferenceEquals(a.BorderGroup, b.BorderGroup);
}
