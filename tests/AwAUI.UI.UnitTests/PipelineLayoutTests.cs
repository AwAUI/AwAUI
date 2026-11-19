namespace AwAUI.UI.UnitTests;

/// <summary>样式 → 布局：计算样式映射为 LayoutStyle，文本叶子经 TextLayout 测量（TestTextEngine：每字符 1em）。</summary>
public class PipelineLayoutTests
{
    [Fact]
    public void Flex_row_of_divs_and_text_has_exact_boxes()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "row", Class = "row", Children =
            [
                new Div { Id = "a", Class = "a" },
                new Text("abc") { Id = "t" },
                new Div { Id = "b", Class = "b" },
            ],
        }, ".row { display: flex; gap: 10px; padding: 5px; align-items: flex-start; }"
           + ".a { width: 50px; height: 20px; } .b { flex: 1; height: 30px; }"
           + "text { font-size: 10px; }");
        Assert.Equal(new Rect(0, 0, 400, 40), h.Box("row"));
        Assert.Equal(new Rect(5, 5, 50, 20), h.Box("a"));
        // 3 个字符 × 10px，行高 = 上升 0.8em + 下降 0.2em = 10px
        Assert.Equal(new Rect(65, 5, 30, 10), h.Box("t"));
        Assert.Equal(new Rect(105, 5, 290, 30), h.Box("b"));
    }

    [Fact]
    public void Column_with_wrapping_text_and_percentages()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "col", Children =
            [
                new Text("aaaa bbbb cccc") { Id = "t" },
                new Div { Id = "half" },
            ],
        }, "#col { display: flex; flex-direction: column; width: 100px; padding: 0 10px; }"
           + "#t { font-size: 10px; line-height: 20px; }"
           + "#half { width: 50%; height: calc(10% + 3px); margin-left: auto; }");
        // 内容宽 80px：每行 8 个字符 → "aaaa " / "bbbb " / "cccc" 三行，每行 20px
        Assert.Equal(new Rect(10, 0, 80, 60), h.Box("t"));
        // 50% of 80 = 40，靠右（margin-left: auto）；高度百分比的基准不确定 → 只剩 3px？calc(10% + 3px) 按 auto 处理
        var half = h.Box("half");
        Assert.Equal(50, half.X);
        Assert.Equal(40, half.Width);
    }

    [Fact]
    public void Button_centers_text_with_ua_styles()
    {
        var h = new PipelineHarness(new Button { Id = "b", Children = ["Go"] }, "button { font-size: 10px; padding: 4px 8px; border-width: 1px; }");
        var b = h.Box("b");
        // inline-flex：宽度收缩到内容 2×10 + 16 + 2；高度 10 + 8 + 2
        Assert.Equal(new Rect(0, 0, 38, 20), b);
        var text = h.El("b").Children[0];
        Assert.Equal(new Rect(9, 5, 20, 10), text.BoundingRect);
    }

    [Fact]
    public void Display_none_hides_subtree_and_inline_style_applies()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "root", Children =
            [
                new Div { Id = "gone", Style = "display: none", Children = [new Div { Id = "inner", Style = "height: 10px" }] },
                new Div { Id = "x", Style = "height: 12px; width: 30px" },
            ],
        });
        Assert.Equal(default, h.Box("gone"));
        Assert.Equal(default, h.Box("inner"));
        Assert.Equal(new Rect(0, 0, 30, 12), h.Box("x"));
    }

    [Fact]
    public void Absolute_and_relative_positioning()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "host", Style = "position: relative; width: 200px; height: 100px; margin-left: 10px",
            Children =
            [
                new Div { Id = "abs", Style = "position: absolute; right: 10px; bottom: 20px; width: 30px; height: 40px" },
                new Div { Id = "rel", Style = "position: relative; left: 5px; top: -3px; height: 10px" },
            ],
        });
        Assert.Equal(new Rect(10, 0, 200, 100), h.Box("host"));
        Assert.Equal(new Rect(170, 40, 30, 40), h.Box("abs"));
        Assert.Equal(new Rect(15, -3, 200, 10), h.Box("rel"));
    }

    [Fact]
    public void Percentage_padding_that_resolves_negative_is_clamped_at_layout_time()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "width: 100px",
            Children = [new Div { Id = "p", Style = "padding-left: calc(10% - 50px); padding-top: calc(20% - 10px); height: 0" }],
        });
        // calc(10% - 50px) = -40px → 0；calc(20% - 10px) = 10px
        Assert.Equal(new Rect(0, 0, 100, 10), h.Box("p"));
    }

    [Fact]
    public void Inline_block_shrinks_and_stacks_children_like_a_block()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "ib", Style = "display: inline-block; padding: 2px",
            Children = [new Text("abcd") { Id = "a", Style = "font-size: 10px" }, new Text("ab") { Id = "b", Style = "font-size: 10px" }],
        });
        Assert.Equal(new Rect(0, 0, 44, 24), h.Box("ib"));
        Assert.Equal(new Rect(2, 2, 40, 10), h.Box("a"));
        Assert.Equal(new Rect(2, 12, 40, 10), h.Box("b"));
    }

    [Fact]
    public void Input_and_image_boxes()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "display: flex; align-items: flex-start",
            Children =
            [
                new Input { Id = "i", Placeholder = "type", Style = "font-size: 10px; padding: 3px; min-width: 0; width: 80px" },
                new Image { Id = "img", Source = "x.png", Style = "width: 16px; height: 16px" },
            ],
        });
        // 一行高 10px + 上下内边距 6px + 边框 2px
        Assert.Equal(new Rect(0, 0, 80, 18), h.Box("i"));
        Assert.Equal(new Rect(80, 0, 16, 16), h.Box("img"));
    }

    [Fact]
    public void Text_rewraps_when_viewport_shrinks()
    {
        var h = new PipelineHarness(new Text("aaaa bbbb") { Id = "t", Style = "font-size: 10px" }, width: 200);
        Assert.Equal(10, h.Box("t").Height);
        h.Pipeline.SetViewport(new Size(60, 300), 1);
        h.Frame();
        Assert.Equal(new Rect(0, 0, 60, 20), h.Box("t"));
    }
}
