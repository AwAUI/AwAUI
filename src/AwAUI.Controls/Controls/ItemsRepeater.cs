using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>
/// A virtualization panel that creates elements only for the visible rows and their overscan.
/// It reads items by index, without copying the source or enumerating offscreen items.
/// Set <see cref="ItemHeight"/> for fixed-height rows (all calculations are O(1)); set it to <c>null</c> to measure
/// variable-height rows after layout, using <see cref="EstimatedItemHeight"/> until each row has been realized once.
/// Set <see cref="Lanes"/> above 1 to lay the realized items out in a virtualized grid; the item index maps to
/// row <c>index / Lanes</c> and column <c>index % Lanes</c>.
/// </summary>
public sealed partial class ItemsRepeater : Control
{
    VirtualizerHost? _host;

    /// <summary>Assign a new source or raise collection change notifications on the UI thread to refresh the list.</summary>
    public IList? ItemsSource { get; set; }

    /// <summary>Creates the content of one realized row. Defaults to presenting a node or formatted text.</summary>
    public Func<object?, Node>? ItemTemplate { get; set; }

    /// <summary>Optional unique, stable keys for keeping row identity across collection changes. The default key is the row index.</summary>
    public Func<object?, object>? ItemKey { get; set; }

    /// <summary>
    /// Height of every row in logical pixels, or <c>null</c> to measure each row after layout (variable-height mode).
    /// Fixed-height rows are clipped to these bounds; variable-height rows report their content height back to the list.
    /// Ignored when <see cref="Orientation"/> is <see cref="Orientation.Horizontal"/>.
    /// </summary>
    public double? ItemHeight { get; set; } = 32;

    /// <summary>Height assumed for rows that have not been measured yet in variable-height mode.</summary>
    public double EstimatedItemHeight { get; set; } = 32;

    /// <summary>
    /// Width of every item in logical pixels when <see cref="Orientation"/> is <see cref="Orientation.Horizontal"/>,
    /// or <c>null</c> to measure each item after layout. Ignored in the default vertical mode.
    /// </summary>
    public double? ItemWidth { get; set; } = 32;

    /// <summary>Width assumed for items that have not been measured yet in horizontal variable-width mode.</summary>
    public double EstimatedItemWidth { get; set; } = 32;

    /// <summary>
    /// Main axis of the virtualized list. <see cref="Orientation.Vertical"/> scrolls down and stacks items in rows;
    /// <see cref="Orientation.Horizontal"/> scrolls right and stacks items in columns. Horizontal mode uses
    /// <see cref="ItemWidth"/> / <see cref="EstimatedItemWidth"/> and requires a constrained width.
    /// </summary>
    public Orientation Orientation { get; set; } = Primitives.Orientation.Vertical;

    /// <summary>Number of rows mounted just outside each edge of the viewport.</summary>
    public int Overscan { get; set; } = 2;

    /// <summary>
    /// Number of columns. The item at index <c>i</c> is placed in row <c>i / Lanes</c> and column <c>i % Lanes</c>;
    /// a row's height is the tallest realized item in it. Values below 1 are treated as 1.
    /// </summary>
    public int Lanes { get; set; } = 1;

    /// <summary>Vertical gap in logical pixels: between rows in vertical mode, between lanes (rows) in horizontal mode.</summary>
    public double RowGap { get; set; }

    /// <summary>Horizontal gap in logical pixels: between items in horizontal mode, between columns when <see cref="Lanes"/> is above 1 in vertical mode.</summary>
    public double ColumnGap { get; set; }

    /// <summary>Optional controller for scrolling to content outside the currently realized range.</summary>
    public ScrollController? ScrollController { get; set; }

    VirtualizerHost Host => _host ??= new VirtualizerHost(InvalidateFromLayout);

    protected override Node Build()
    {
        var source = ItemsSource;
        bool horizontal = Orientation == Orientation.Horizontal;
        double? requestedSize = horizontal ? ItemWidth : ItemHeight;
        bool fixedSize = requestedSize is double requested && requested > 0 && !double.IsInfinity(requested);
        double size = fixedSize ? requestedSize!.Value : 0;
        double estimated = horizontal ? EstimatedItemWidth : EstimatedItemHeight;
        double estimate = estimated > 0 && !double.IsInfinity(estimated) ? estimated : 32;
        double mainGap = horizontal ? ColumnGap : RowGap;
        double gap = mainGap > 0 && !double.IsInfinity(mainGap) ? mainGap : 0;
        int lanes = Math.Max(1, Lanes);
        var host = Host;
        host.Mount(Root);
        host.UseController(ScrollController);
        // 定长、变长与列数都是主轴的项结构：SetOptions 在结构变化时丢弃测量
        host.SetOptions(new VirtualizerOptions
        {
            ItemSize = fixedSize ? size : null,
            EstimatedItemSize = estimate,
            Overscan = Math.Max(0, Overscan),
            Lanes = lanes,
            Gap = gap,
            Horizontal = horizontal,
        });
        int count = source?.Count ?? 0;
        var items = host.Begin(count);

        UseEffect(() =>
        {
            if (source is not INotifyCollectionChanged observable) return null;
            NotifyCollectionChangedEventHandler handler = (_, _) => Invalidate();
            observable.CollectionChanged += handler;
            return () => observable.CollectionChanged -= handler;
        }, source);

        var rows = new Div
        {
            Style = horizontal
                ? "position:relative;width:" + Pixels(host.Virtualizer.TotalSize) + ";height:100%"
                : "position:relative;height:" + Pixels(host.Virtualizer.TotalSize),
        };
        rows.Attributes["part"] = "extent";
        if (source is not null)
        {
            string setSize = count.ToString(CultureInfo.InvariantCulture);
            // 变长项不写固定尺寸：布局后按内容尺寸测量，再把尺寸写回虚拟化器
            string itemStyle = "position:absolute;box-sizing:border-box;overflow:clip;"
                + (fixedSize ? (horizontal ? "width:" : "height:") + Pixels(size) + ";" : "");
            foreach (var item in items)
            {
                var value = source[item.Index];
                var reference = new Ref<Element>();
                host.Track(item.Index, reference);
                var row = new Div
                {
                    Ref = reference,
                    Key = ItemKey?.Invoke(value) ?? item.Index,
                    Role = Role.ListItem,
                    Style = itemStyle
                        + (horizontal
                            ? "inset-inline-start:" + Pixels(item.Start) + ";" + LaneStyleHorizontal(item.Lane, lanes)
                            : "top:" + Pixels(item.Start) + ";" + LaneStyle(item.Lane, lanes)),
                    Children = [ItemTemplate?.Invoke(value) ?? Node.From(value)],
                };
                row.Attributes["part"] = "item";
                row.Attributes["aria-posinset"] = (item.Index + 1).ToString(CultureInfo.InvariantCulture);
                row.Attributes["aria-setsize"] = setSize;
                rows.Children.Add(row);
            }
        }

        return Apply(new Div
        {
            Role = Role.List,
            TabIndex = 0,
            ScrollController = host.Controller,
            Style = horizontal
                ? "overflow-y:hidden;overflow-x:auto;max-width:240px;"
                : "overflow-x:hidden;overflow-y:auto;max-height:240px;",
            Children = [rows],
        }, "aw-items-repeater");
    }

    /// <summary>
    /// 水平模式下每条 lane 是一行：单条 lane 撑满高度；多条时按行定位，行间距取 <see cref="RowGap"/>。
    /// 主轴用 <c>inset-inline-start</c>，RTL 下第一项从右边缘开始，与滚动容器的行内起点一致（见 <c>ScrollController</c> 的偏移约定）。
    /// </summary>
    string LaneStyleHorizontal(int lane, int lanes)
    {
        if (lanes == 1) return "inset-block:0;";
        double height = 100.0 / lanes;
        double gap = RowGap > 0 && !double.IsInfinity(RowGap) ? RowGap : 0;
        if (gap == 0)
            return "top:" + Percent(lane * height) + ";height:" + Percent(height) + ";";
        double totalGap = (lanes - 1) * gap;
        return "top:calc(" + Percent(lane * height) + " + " + Pixels(lane * gap / lanes) + ");height:calc("
            + Percent(height) + " - " + Pixels(totalGap / lanes) + ");";
    }

    /// <summary>
    /// 单列时占满整行；多列时按列定位。列间距写成「百分比 ± 固定 px」的组合（先在本侧算出系数），
    /// 避免依赖布局引擎对 calc 乘除法或百分比与 px 混合的支持程度。
    /// </summary>
    string LaneStyle(int lane, int lanes)
    {
        if (lanes == 1) return "inset-inline:0;";
        double width = 100.0 / lanes;
        double gap = ColumnGap > 0 && !double.IsInfinity(ColumnGap) ? ColumnGap : 0;
        if (gap == 0)
            return "inset-inline-start:" + Percent(lane * width) + ";width:" + Percent(width) + ";";
        double totalGap = (lanes - 1) * gap;
        return "inset-inline-start:calc(" + Percent(lane * width) + " + " + Pixels(lane * gap / lanes) + ");width:calc("
            + Percent(width) + " - " + Pixels(totalGap / lanes) + ");";
    }

    static string Percent(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture) + "%";

    protected override void OnMount() => _host?.Mount(Root);

    protected override void OnUnmount() => _host?.Unmount();

    static string Pixels(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture) + "px";
}
