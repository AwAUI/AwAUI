using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// flex / grid 容器的子元素按 order 修正后的文档顺序绘制（CSS Display 3 §3「order-modified document order」），
/// z-index 相同的层叠项也按这个顺序；命中测试与绘制一致（绘制在上面的元素先命中）。
/// </summary>
public class PaintOrderTests
{
    static Color C(string hex) => Color.Parse(hex);

    // a 在文档中靠前但 order:1，排在 b 之后，并用负的左外边距与 b 重叠 20px（x = 40..60）
    const string FlexCss = """
        #f { display: flex; width: 200px; height: 50px; }
        #a { order: 1; width: 60px; height: 50px; margin-left: -20px; background-color: #ff0000; }
        #b { width: 60px; height: 50px; background-color: #0000ff; }
        """;

    static Node FlexScene() => new Div { Id = "f", Children = [new Div { Id = "a" }, new Div { Id = "b" }] };

    [Fact]
    public void Flex_items_paint_and_hit_test_in_order_modified_document_order()
    {
        var h = new PipelineHarness(FlexScene(), FlexCss, defaultSheet: false);
        Assert.Equal(new Rect(40, 0, 60, 50), h.Box("a"));
        Assert.Equal(new Rect(0, 0, 60, 50), h.Box("b"));
        using var bmp = h.Render();
        ColorAssert.Near(C("#0000ff"), bmp.GetPixel(10, 25));
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(50, 25)); // 重叠处：order 靠后的 a 在上面
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(80, 25));
        Assert.Equal("b", h.Pipeline.HitTest(new Point(10, 25))!.Id);
        Assert.Equal("a", h.Pipeline.HitTest(new Point(50, 25))!.Id);
    }

    [Fact]
    public void Z_index_ties_between_flex_items_follow_order_modified_document_order()
    {
        // 两个项目都是 z-index:1 的层叠上下文：z 相同，按 order 修正后的文档顺序
        var h = new PipelineHarness(FlexScene(), FlexCss + " #a, #b { z-index: 1; }", defaultSheet: false);
        using var bmp = h.Render();
        ColorAssert.Near(C("#ff0000"), bmp.GetPixel(50, 25));
        Assert.Equal("a", h.Pipeline.HitTest(new Point(50, 25))!.Id);
        // 定位后代（z 为 auto）同样按这个顺序收集
        var h2 = new PipelineHarness(FlexScene(), FlexCss + " #a, #b { position: relative; }", defaultSheet: false);
        using var bmp2 = h2.Render();
        ColorAssert.Near(C("#ff0000"), bmp2.GetPixel(50, 25));
        Assert.Equal("a", h2.Pipeline.HitTest(new Point(50, 25))!.Id);
    }

    [Fact]
    public void Changing_order_of_overlapping_grid_items_repaints_without_a_layout_change()
    {
        // 两个项目放在同一个网格区域：改 order 不改变布局，只改变绘制顺序（增量绘制的缓存必须失效）
        const string css = """
            #g { display: grid; grid-template-columns: 50px; grid-template-rows: 50px; }
            #g > div { grid-area: 1 / 1; }
            #a { background-color: #ff0000; }
            #b { background-color: #0000ff; }
            .late { order: 1; }
            """;
        var late = new State<bool>(true);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div { Id = "g", Children = [new Div { Id = "a", Class = late.Value ? "late" : null }, new Div { Id = "b" }] },
        }, css, defaultSheet: false);
        using (var bmp = h.Render()) ColorAssert.Near(C("#ff0000"), bmp.GetPixel(25, 25));
        Assert.Equal("a", h.Pipeline.HitTest(new Point(25, 25))!.Id);

        late.Value = false;
        h.Frame();
        Assert.Equal(new Rect(0, 0, 50, 50), h.Box("a"));
        using (var bmp = h.Render()) ColorAssert.Near(C("#0000ff"), bmp.GetPixel(25, 25));
        Assert.Equal("b", h.Pipeline.HitTest(new Point(25, 25))!.Id);

        late.Value = true;
        h.Frame();
        using (var bmp = h.Render()) ColorAssert.Near(C("#ff0000"), bmp.GetPixel(25, 25));
        Assert.Equal("a", h.Pipeline.HitTest(new Point(25, 25))!.Id);

        // 与一次完整的非增量绘制逐像素一致
        var full = new PipelineHarness(new Div { Id = "g", Children = [new Div { Id = "a", Class = "late" }, new Div { Id = "b" }] }, css, defaultSheet: false);
        using var expected = full.Render();
        using var actual = h.Render();
        Assert.Equal(expected.GetPixel(25, 25), actual.GetPixel(25, 25));
    }

    [Fact]
    public void Block_container_keeps_document_order_even_with_order_set()
    {
        // order 只对 flex / grid 项目有效
        const string css = """
            #c, #c > div { display: block; }
            #c > div { width: 60px; height: 50px; }
            #a { order: 1; background-color: #ff0000; }
            #b { margin-top: -50px; margin-left: 20px; background-color: #0000ff; }
            """;
        var h = new PipelineHarness(new Div { Id = "c", Children = [new Div { Id = "a" }, new Div { Id = "b" }] }, css, defaultSheet: false);
        using var bmp = h.Render();
        ColorAssert.Near(C("#0000ff"), bmp.GetPixel(40, 25));
        Assert.Equal("b", h.Pipeline.HitTest(new Point(40, 25))!.Id);
    }
}
