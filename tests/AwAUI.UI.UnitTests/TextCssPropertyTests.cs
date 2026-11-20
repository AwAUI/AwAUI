using AwAUI.Graphics;
using AwAUI.Text;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 文本属性的 UI 接入（第九批）：letter-spacing / word-spacing / text-indent / tab-size / text-wrap / -webkit-line-clamp、
/// 文本装饰（含传播）与 text-shadow、字体特性映射、display 的新取值、输入框的 field-sizing。TestTextEngine：每字符 1em。
/// </summary>
public class TextCssPropertyTests
{
    static TextLayout Para(PipelineHarness h, string id)
    {
        var e = h.El(id);
        var box = RenderBox.Get(e);
        if (box.Paragraph is { } p) return p.Layout;
        // 容器中的匿名文本
        return RenderBox.Get(e.Children[0]).Paragraph!.Layout;
    }

    static TextStyle StyleOf(PipelineHarness h, string id) => Para(h, id).GetStyleAt(0);

    [Fact]
    public void Letter_and_word_spacing_change_measured_width()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "display: flex; flex-direction: column; align-items: flex-start; font-size: 10px",
            Children =
            [
                new Text("abc") { Id = "plain" },
                new Text("abc") { Id = "ls", Style = "letter-spacing: 2px" },
                new Text("a b c") { Id = "ws", Style = "word-spacing: 5px" },
            ],
        });
        Assert.Equal(30, h.Box("plain").Width);
        Assert.Equal(36, h.Box("ls").Width);
        // 5 个字符 50px + 两个空格各 5px
        Assert.Equal(60, h.Box("ws").Width);
    }

    [Fact]
    public void Text_indent_shifts_first_line_and_reduces_its_width()
    {
        var h = new PipelineHarness(new Text("aaaa bbbb") { Id = "t", Style = "width: 60px; font-size: 10px; text-indent: 20px" });
        var tl = Para(h, "t");
        Assert.Equal(2, tl.LineCount);
        Assert.Equal(20, tl.GetLine(0).Left);
        Assert.Equal(0, tl.GetLine(1).Left);
        // 百分比相对可用宽度；hanging 反转
        var h2 = new PipelineHarness(new Text("aaaa bbbb") { Id = "t", Style = "width: 100px; font-size: 10px; text-indent: 10% hanging" });
        var tl2 = Para(h2, "t");
        Assert.Equal(0, tl2.GetLine(0).Left);
    }

    [Fact]
    public void Tab_size_in_preserved_white_space()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "display: flex; flex-direction: column; align-items: flex-start; font-size: 10px; white-space: pre",
            Children =
            [
                new Text("\tx") { Id = "a", Style = "tab-size: 4" },
                new Text("\tx") { Id = "b", Style = "tab-size: 25px" },
            ],
        });
        Assert.Equal(50, h.Box("a").Width);
        Assert.Equal(35, h.Box("b").Width);
    }

    [Fact]
    public void Text_wrap_mode_nowrap_and_balance()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "width: 100px; font-size: 10px",
            Children =
            [
                new Text("aaa bbb ccc ddd") { Id = "nowrap", Style = "text-wrap-mode: nowrap" },
                new Text("aaa bbb ccc ddd") { Id = "wrap" },
                new Text("aaa bbb ccc ddd") { Id = "balance", Style = "text-wrap: balance" },
            ],
        });
        Assert.Equal(1, Para(h, "nowrap").LineCount);
        var wrap = Para(h, "wrap");
        Assert.Equal(2, wrap.LineCount);
        // 贪心：第一行 "aaa bbb " 放下两个词（"aaa bbb ccc" 宽 110 放不下），均衡后两行各两个词
        var bal = Para(h, "balance");
        Assert.Equal(2, bal.LineCount);
        Assert.Equal(70, bal.GetLine(0).Width);
        Assert.Equal(70, bal.GetLine(1).Width);
        // 不均衡的文本：贪心第一行 9 字符、第二行 1 个词
        var h2 = new PipelineHarness(new Text("aa bb cc dd ee") { Id = "t", Style = "width: 110px; font-size: 10px; text-wrap: balance" });
        var t = Para(h2, "t");
        Assert.Equal(2, t.LineCount);
        Assert.True(Math.Abs(t.GetLine(0).Width - t.GetLine(1).Width) <= 30, $"{t.GetLine(0).Width} vs {t.GetLine(1).Width}");
    }

    [Fact]
    public void Webkit_line_clamp_limits_lines_with_ellipsis()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "p", Class = "line-clamp-2",
            Children = ["aaaa bbbb cccc dddd eeee"],
        }, ".line-clamp-2 { width: 50px; font-size: 10px; overflow: hidden; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2 }");
        var tl = Para(h, "p");
        Assert.Equal(2, tl.LineCount);
        Assert.True(tl.DidExceedMaxLines);
        Assert.Equal(20, h.Box("p").Height);
        // 最后一行 "bbbb" 加省略号（省略号不在文本中，按行宽判断：5 个字符）
        Assert.Equal(50, tl.GetLine(1).Width);
    }

    [Fact]
    public void Display_table_family_and_list_item_map_to_block_like_layout()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "width: 200px",
            Children =
            [
                new Div { Id = "row", Style = "display: table-row", Children = [new Div { Id = "c1", Style = "display: table-cell; width: 30px" }, new Div { Id = "c2", Style = "display: table-cell; width: 40px; height: 20px" }] },
                new Div { Id = "li", Style = "display: list-item; height: 5px" },
                new Div { Id = "it", Style = "display: inline-table", Children = [new Div { Style = "width: 25px; height: 5px" }] },
            ],
        });
        Assert.Equal(new Rect(0, 0, 30, 20), h.Box("c1"));
        Assert.Equal(new Rect(30, 0, 40, 20), h.Box("c2"));
        Assert.Equal(200, h.Box("li").Width);
        Assert.Equal(25, h.Box("it").Width);
    }

    [Fact]
    public void Decorations_map_and_propagate_to_descendant_text()
    {
        var on = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "link", Class = on.Value ? "u hover" : "u",
                Children = [new Text("ab") { Id = "inner" }, "cd"],
            },
        }, ".u { font-size: 10px; color: #112233 } .u.hover { text-decoration: underline wavy #ff0000 2px; text-underline-offset: 3px }"
           + " #inner { text-decoration-line: line-through }");
        Assert.Equal(TextDecorationLine.LineThrough, StyleOf(h, "inner").Decoration);
        on.Value = true;
        h.Frame();
        // 传播：子元素的文字也带上父元素的下划线（颜色、样式取装饰盒的）
        var inner = StyleOf(h, "inner");
        Assert.Equal(TextDecorationLine.LineThrough | TextDecorationLine.Underline, inner.Decoration);
        var anon = RenderBox.Get(h.El("link").Children[1]).Paragraph!.Layout.GetStyleAt(0);
        Assert.Equal(TextDecorationLine.Underline, anon.Decoration);
        Assert.Equal(TextDecorationStyle.Wavy, anon.DecorationStyle);
        Assert.Equal(Color.Parse("#ff0000"), anon.DecorationColor);
        Assert.Equal(2, anon.DecorationThickness);
        Assert.Equal(3, anon.UnderlineOffset);
        on.Value = false;
        h.Frame();
        Assert.Equal(TextDecorationLine.LineThrough, StyleOf(h, "inner").Decoration);
    }

    [Fact]
    public void Decoration_does_not_propagate_into_absolutely_positioned_or_inline_block_children()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "position: relative; text-decoration: underline; font-size: 10px",
            Children =
            [
                new Text("a") { Id = "flow" },
                new Text("b") { Id = "abs", Style = "position: absolute" },
                new Text("c") { Id = "ib", Style = "display: inline-block" },
            ],
        });
        Assert.Equal(TextDecorationLine.Underline, StyleOf(h, "flow").Decoration);
        Assert.Equal(TextDecorationLine.None, StyleOf(h, "abs").Decoration);
        Assert.Equal(TextDecorationLine.None, StyleOf(h, "ib").Decoration);
    }

    [Fact]
    public void Text_shadow_features_variations_and_transform_map_to_text_style()
    {
        var h = new PipelineHarness(new Text("ab") { Id = "t" },
            "#t { font-size: 10px; color: #000000; text-shadow: 1px 2px 0 #ff0000, 0 0 4px rgb(0 0 255 / 50%);"
            + " font-variant-numeric: tabular-nums slashed-zero; font-variant-caps: small-caps; font-kerning: none;"
            + " font-feature-settings: 'liga' 0, 'ss01'; font-variation-settings: 'wght' 650; text-transform: uppercase;"
            + " -webkit-text-fill-color: #00ff00 }");
        var s = StyleOf(h, "t");
        Assert.NotNull(s.Shadows);
        Assert.Equal(2, s.Shadows!.Count);
        Assert.Equal(new TextShadow(1, 2, 0, Color.Parse("#ff0000")), s.Shadows[0]);
        Assert.Equal(4, s.Shadows[1].Blur);
        var tags = s.Features!.Select(f => OpenTypeTagName(f.Tag) + "=" + f.Value).ToArray();
        Assert.Equal(new[] { "tnum=1", "zero=1", "smcp=1", "kern=0", "liga=0", "ss01=1" }, tags);
        Assert.Equal(1, s.Variation.Settings.Length);
        Assert.Equal(650f, s.Variation.Settings[0].Value);
        Assert.Equal(TextTransform.Uppercase, s.TextTransform);
        Assert.Equal(Color.Parse("#00ff00"), s.Color);
    }

    static string OpenTypeTagName(uint tag) =>
        new string(new[] { (char)(tag >> 24), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF) });

    [Fact]
    public void Text_shadow_is_painted_below_text()
    {
        var h = new PipelineHarness(new Text("a") { Id = "t" },
            "#t { font-size: 20px; line-height: 20px; color: #000000; text-shadow: 4px 4px 0 #ff0000 }", width: 40, height: 40);
        using var bmp = h.Render();
        // 方块字形覆盖 (0..20) 区域的上伸部分；阴影向右下偏移 4px，伸出字形之外的那一条是红色
        bool red = false;
        for (int y = 0; y < 30 && !red; y++)
        {
            var c = bmp.GetPixel(22, y);
            red = c.R > 0.9f && c.G < 0.1f && c.B < 0.1f;
        }
        Assert.True(red);
    }

    [Fact]
    public void Underline_position_under_and_offset_move_the_line()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "display: flex; flex-direction: column; align-items: flex-start; font-size: 20px; line-height: 40px; text-decoration: underline; text-decoration-color: #ff0000; text-decoration-thickness: 2px",
            Children =
            [
                new Text("a") { Id = "offset", Style = "text-underline-offset: 6px" },
            ],
        }, width: 60, height: 60);
        using var bmp = h.Render();
        // 行高 40、字号 20：基线在 10 + 16 = 26；下划线顶边 26 + 6 = 32
        var below = bmp.GetPixel(10, 33);
        Assert.True(below.R > 0.9f && below.G < 0.1f, below.ToString());
        var above = bmp.GetPixel(10, 30);
        Assert.False(above.R > 0.9f && above.G < 0.1f && above.B < 0.1f, above.ToString());
    }

    [Fact]
    public void Input_field_sizing_fixed_by_default_and_content_grows()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "display: flex; flex-direction: column; align-items: flex-start",
            Children =
            [
                new Input { Id = "fixed", Value = new string('a', 30) },
                new Input { Id = "content", Value = new string('a', 30), Style = "field-sizing: content" },
            ],
        }, "input { font-size: 10px; padding: 0; border: 0; min-width: 50px }");
        Assert.Equal(50, h.Box("fixed").Width);
        Assert.Equal(300, h.Box("content").Width);
    }
}

/// <summary>段落中行内元素的 vertical-align（第九批）：sub / super / 长度换算为基线偏移，行盒随之增高。</summary>
public class InlineVerticalAlignTests
{
    [Fact]
    public void Sub_super_and_length_shift_the_baseline_of_inline_spans()
    {
        var h = new PipelineHarness(new Text
        {
            Id = "t",
            Children = ["a", new Span("2") { Id = "sup", Style = "vertical-align: super" }, new Span("x") { Id = "sub", Style = "vertical-align: sub" },
                new Span("y") { Id = "len", Style = "vertical-align: -3px" }],
        }, "#t { font-size: 10px; line-height: 10px }");
        var tl = RenderBox.Get(h.El("t")).Paragraph!.Layout;
        Assert.Equal(3.4f, tl.GetStyleAt(1).BaselineShift, 0.01f);
        Assert.Equal(-2f, tl.GetStyleAt(2).BaselineShift, 0.01f);
        Assert.Equal(-3f, tl.GetStyleAt(3).BaselineShift, 0.01f);
        // 行盒增高：上标抬高 3.4、下移最多 3
        Assert.Equal(16, h.Box("t").Height); // 10 + 3.4 + 3 = 16.4，按设备像素对齐
    }
}
