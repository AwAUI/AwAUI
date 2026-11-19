using AwAUI.Layout;

namespace AwAUI.UI.UnitTests;

/// <summary><see cref="LayoutMapper"/> 对网格、order、justify-* 与替换元素的映射。</summary>
public class LayoutMappingGridTests
{
    static LayoutStyle Mapped(PipelineHarness h, string id) => RenderBox.Get(h.El(id)).Layout!.Style;

    [Fact]
    public void Grid_container_and_item_properties_are_mapped()
    {
        var h = new PipelineHarness(new Div { Id = "g", Children = [new Div { Id = "i" }] },
            "#g { display: inline-grid; grid-template-columns: [a] 10px repeat(auto-fill, [b] 20px) [c]; grid-template-rows: repeat(2, 1fr);"
            + " grid-template-areas: 'x y'; grid-auto-flow: column dense; grid-auto-columns: 30px; justify-items: end; gap: 3px 4px; }"
            + "#i { grid-area: x; order: 2; justify-self: center; align-self: start; }");
        var g = Mapped(h, "g");
        Assert.Equal(Display.InlineGrid, g.Display);
        Assert.Equal(GridAutoFlow.ColumnDense, g.GridAutoFlow);
        Assert.Equal(JustifyItems.End, g.JustifyItems);
        Assert.Equal(LengthPercentage.Px(3), g.RowGap);
        Assert.Equal(LengthPercentage.Px(4), g.ColumnGap);
        var cols = g.GridTemplateColumns!;
        // [a] 10px | repeat(auto-fill, [b] 20px) | [c]（只有线名的段）
        Assert.Equal(3, cols.Segments.Count);
        Assert.Equal("a", cols.Segments[0].LineNames[0][0]);
        Assert.Equal(GridRepeatKind.AutoFill, cols.Segments[1].Kind);
        Assert.Equal("b", cols.Segments[1].LineNames[0][0]);
        Assert.Empty(cols.Segments[2].Tracks);
        Assert.Equal("c", cols.Segments[2].LineNames[0][0]);
        Assert.Equal(2, g.GridTemplateRows!.Segments[0].Count);
        Assert.Equal(GridTrackSize.Fr(1), g.GridTemplateRows.Segments[0].Tracks[0]);
        Assert.Equal(1, g.GridTemplateAreas!.Rows);
        Assert.Equal(GridTrackSize.Px(30), g.GridAutoColumns!.Segments[0].Tracks[0]);
        Assert.Null(g.GridAutoRows);

        var i = Mapped(h, "i");
        Assert.Equal(GridLine.Named("x"), i.GridRowStart);
        Assert.Equal(GridLine.Named("x"), i.GridColumnEnd);
        Assert.Equal(2, i.Order);
        Assert.Equal(JustifySelf.Center, i.JustifySelf);
        Assert.Equal(AlignSelf.Start, i.AlignSelf);
    }

    [Fact]
    public void Equal_grid_values_map_to_equal_styles_without_relayout()
    {
        // 两个元素用同一条规则：计算值实例共享，映射结果缓存后是同一个 GridTrackList
        var h = new PipelineHarness(new Div { Children = [new Div { Id = "a", Class = "g" }, new Div { Id = "b", Class = "g" }] },
            ".g { display: grid; grid-template-columns: repeat(3, minmax(10px, 1fr)); }");
        Assert.Same(Mapped(h, "a").GridTemplateColumns, Mapped(h, "b").GridTemplateColumns);
        // 内容相同但来自不同规则：按值相等
        var h2 = new PipelineHarness(new Div { Id = "c", Class = "g" }, ".g { display: grid; grid-template-columns: repeat(3, minmax(10px, 1fr)); }");
        Assert.Equal(Mapped(h, "a").GridTemplateColumns, Mapped(h2, "c").GridTemplateColumns);
        Assert.Equal(Mapped(h, "a"), Mapped(h2, "c"));
    }

    [Fact]
    public void Non_grid_containers_ignore_grid_container_properties()
    {
        var h = new PipelineHarness(new Div { Id = "b" }, "#b { display: block; grid-template-columns: 10px; }");
        Assert.Null(Mapped(h, "b").GridTemplateColumns);
        Assert.Equal(Display.Block, Mapped(h, "b").Display);
    }

    [Fact]
    public void Paint_order_follows_order_modified_document_order()
    {
        var kids = Enumerable.Range(0, 40).Select(i => (Node)new Div { Id = "k" + i, Class = i % 3 == 0 ? "late" : i == 7 ? "early" : null }).ToList();
        var h = new PipelineHarness(new Div { Children = [new Div { Id = "flex", Children = [.. kids] }, new Div { Id = "block", Children = [new Div { Id = "x", Class = "late" }, new Div { Id = "y" }] }] },
            "#flex { display: flex; } #flex > div { width: 5px; height: 5px; } .late { order: 1; } .early { order: -1; }");
        var order = LayoutMapper.PaintOrder(h.El("flex"), new List<Element>()).Select(e => e.Id!).ToList();
        var expected = new[] { "k7" }.Concat(Enumerable.Range(0, 40).Where(i => i % 3 != 0 && i != 7).Select(i => "k" + i))
            .Concat(Enumerable.Range(0, 40).Where(i => i % 3 == 0).Select(i => "k" + i)).ToList();
        Assert.Equal(expected, order);
        // 非 flex / grid 容器：保持文档顺序（且直接返回子元素列表）
        Assert.Same(h.El("block").Children, LayoutMapper.PaintOrder(h.El("block"), new List<Element>()));
        // 布局顺序与之相同
        Assert.True(h.Box("k7").X < h.Box("k1").X);
        Assert.True(h.Box("k39").X > h.Box("k38").X);
    }

    [Fact]
    public void For_replaced_sets_the_flag_and_used_ratio()
    {
        var s = LayoutStyle.Default with { AspectRatio = 2 };
        var r = LayoutMapper.ForReplaced(s, 1.5f);
        Assert.True(r.IsReplaced);
        Assert.Equal(1.5f, r.AspectRatio);
        Assert.Equal(2f, LayoutMapper.ForReplaced(s, null).AspectRatio);
        Assert.Equal(Display.Block, r.Display);
    }
}
