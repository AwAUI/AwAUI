using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 引擎侧 RTL：<c>direction</c> 进入布局与文本（块级对齐、flex 主轴、段落双向方向），
/// HTML <c>dir</c> 属性作为表现提示映射；子元素继承父元素的方向。
/// </summary>
public class DirectionLayoutTests
{
    const string Css = """
        .row { display: flex; width: 200px; gap: 10px; }
        .rtl { direction: rtl; }
        .item { width: 40px; height: 20px; background: #3366ff; }
        .text { width: 200px; height: 20px; font-size: 10px; text-align: start; margin: 0; }
        """;

    static PipelineHarness Row(string? directionClass, string? dirAttribute = null)
    {
        var row = new Div
        {
            Id = "row",
            Class = directionClass is null ? "row" : "row " + directionClass,
            Children =
            [
                new Div { Id = "a", Class = "item" },
                new Div { Id = "b", Class = "item" },
            ],
        };
        if (dirAttribute is not null) row.Attributes["dir"] = dirAttribute;
        var h = new PipelineHarness(row, Css, width: 240, height: 60);
        h.Frame();
        return h;
    }

    [Fact]
    public void Direction_rule_mirrors_the_flex_main_axis()
    {
        var ltr = Row(null);
        var rtl = Row("rtl");

        Assert.True(ltr.Box("a").Left < ltr.Box("b").Left, $"LTR: the first item leads from the left (a={ltr.Box("a")} b={ltr.Box("b")} row={ltr.Box("row")})");
        Assert.True(rtl.Box("a").Left > rtl.Box("b").Left, $"RTL: the first item leads from the right (a={rtl.Box("a")} b={rtl.Box("b")} row={rtl.Box("row")})");
        Assert.Equal(ltr.Box("row").Right - ltr.Box("a").Right, rtl.Box("a").Left - rtl.Box("row").Left, 0.5f);
    }

    [Fact]
    public void Dir_attribute_maps_to_direction_and_is_inherited_by_children()
    {
        var row = new Div
        {
            Id = "outer",
            Attributes = { ["dir"] = "rtl" },
            Children =
            [
                new Div
                {
                    Id = "row",
                    Class = "row",
                    Children =
                    [
                        new Div { Id = "a", Class = "item" },
                        new Div { Id = "b", Class = "item" },
                    ],
                },
            ],
        };
        var h = new PipelineHarness(row, Css, width: 240, height: 60);
        h.Frame();

        // 内层没有 dir，也没有 direction 规则：方向从带 dir 的祖先继承
        Assert.True(h.Box("a").Left > h.Box("b").Left, "RTL inherited from the dir attribute should mirror the row");
    }

    [Fact]
    public void Text_align_start_follows_the_paragraph_direction()
    {
        var ltr = TextProbe("ltr");
        var rtl = TextProbe("rtl");
        using var ltrImage = ltr.Render(Color.White);
        using var rtlImage = rtl.Render(Color.White);
        var ltrBox = ltr.Box("text");
        var rtlBox = rtl.Box("text");

        var (ltrStart, _) = InkBounds(ltrImage, ltrBox);
        Assert.True(ltrStart - ltrBox.Left < 24, $"LTR text should start at the left edge, ink starts at {ltrStart - ltrBox.Left:F1}px");

        var (_, rtlEnd) = InkBounds(rtlImage, rtlBox);
        Assert.True(rtlBox.Right - rtlEnd < 24, $"RTL text should end at the right edge, ink ends {rtlBox.Right - rtlEnd:F1}px before it");
    }

    [Fact]
    public void Logical_padding_maps_to_physical_sides_in_layout()
    {
        const string css = """
            .inset { box-sizing: border-box; width: 100px; padding-inline-start: 10px; padding-inline-end: 20px; }
            .inset > .fill { width: 100%; height: 10px; }
            .rtl { direction: rtl; }
            """;
        Rect Probe(string? direction)
        {
            var box = new Div
            {
                Id = "box",
                Class = direction is null ? "inset" : "inset " + direction,
                Children = [new Div { Id = "fill", Class = "fill" }],
            };
            var h = new PipelineHarness(box, css, width: 200, height: 40);
            h.Frame();
            return h.Box("fill");
        }

        var ltrBox = Probe(null);
        var rtlBox = Probe("rtl");
        // LTR：start 在左（10px），end 在右（20px）
        Assert.Equal(10, ltrBox.Left, 1);
        Assert.Equal(80, ltrBox.Right, 1);
        // RTL：start 在右（10px），end 在左（20px）
        Assert.Equal(20, rtlBox.Left, 1);
        Assert.Equal(90, rtlBox.Right, 1);
    }

    [Fact]
    public void Logical_start_start_radius_moves_to_the_inline_start_corner()
    {
        const string radiusCss = """
            .box { width:40px; height:40px; margin:10px; background:#ff0000; border:2px solid #00aa00;
                   border-start-start-radius: 12px; }
            .rtl { direction: rtl; }
            """;
        PipelineHarness Box(string cls)
        {
            var h = new PipelineHarness(new Div { Id = "box", Class = "box " + cls }, radiusCss, width: 64, height: 64);
            h.Frame();
            return h;
        }

        var ltr = Box("ltr");
        var rtl = Box("rtl");
        using var ltrImage = ltr.Render(Color.White);
        using var rtlImage = rtl.Render(Color.White);
        var box = ltr.Box("box");
        int left = (int)MathF.Round(box.Left);
        int top = (int)MathF.Round(box.Top);
        int right = (int)MathF.Round(box.Right);

        // LTR 的块起始 / 行内起始角是左上，RTL 是右上：圆角外侧露出页面白色，另一侧被绿色边框覆盖
        Assert.True(IsPage(ltrImage.GetPixel(left + 1, top + 1)), "LTR: the top-left corner should be rounded");
        Assert.True(IsBorder(ltrImage.GetPixel(right - 2, top + 1)), "LTR: the top-right corner should stay square");
        Assert.True(IsBorder(rtlImage.GetPixel(left + 1, top + 1)), "RTL: the top-left corner should stay square");
        Assert.True(IsPage(rtlImage.GetPixel(right - 2, top + 1)), "RTL: the top-right corner should be rounded");

        static bool IsPage(Color c) => c.R > 0.9f && c.G > 0.9f && c.B > 0.9f;
        static bool IsBorder(Color c) => c.G > 0.2f && c.R < 0.5f;
    }

    static PipelineHarness TextProbe(string direction) => new(        new Div
        {
            Id = "text",
            Class = "text " + direction,
            Children = [new Text { Content = "Direction" }],
        }, Css, width: 240, height: 40);

    static (int Start, int End) InkBounds(Bitmap bitmap, Rect box)
    {
        int left = Math.Max(0, (int)Math.Floor(box.Left));
        int right = Math.Min(bitmap.Width - 1, (int)Math.Ceiling(box.Right));
        int top = Math.Max(0, (int)Math.Floor(box.Top));
        int bottom = Math.Min(bitmap.Height - 1, (int)Math.Ceiling(box.Bottom));
        int start = int.MaxValue, end = -1;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                if (bitmap.GetPixel(x, y).R < 0.5f)
                {
                    start = Math.Min(start, x);
                    end = Math.Max(end, x);
                }
        Assert.True(end >= 0, "no text ink in the expected box");
        return (start, end);
    }
}
