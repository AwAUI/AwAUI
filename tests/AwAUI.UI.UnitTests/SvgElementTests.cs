using AwAUI.Graphics;
using AwAUI.Svg;

namespace AwAUI.UI.UnitTests;

/// <summary>Svg 元素与 Icon 组件：固有尺寸、currentColor 取 CSS color、录制缓存的键。</summary>
public partial class SvgElementTests
{
    const string TintedIcon = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10' width='10' height='10'><rect width='10' height='10' fill='currentColor'/></svg>";
    const string FixedIcon = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10' width='20' height='10'><rect width='10' height='10' fill='#00ff00'/></svg>";

    [Fact]
    public void Svg_uses_document_size_and_current_color_from_css()
    {
        var doc = SvgDocument.Parse(TintedIcon);
        Assert.True(doc.UsesCurrentColor);
        var h = new PipelineHarness(new Div
        {
            Style = "color: #ff0000",
            Children = [new Svg(doc) { Id = "a" }, new Svg(doc) { Id = "b", Style = "width: 30px; height: 30px; color: #0000ff" }],
        }, "div { display: flex; align-items: flex-start; }");
        Assert.Equal(new Size(10, 10), h.Box("a").Size);
        Assert.Equal(new Size(30, 30), h.Box("b").Size);
        using var bmp = h.Render();
        var a = bmp.GetPixel(5, 5);
        Assert.True(a.R > 0.9f && a.B < 0.1f, a.ToString());
        var b = bmp.GetPixel(25, 15);
        Assert.True(b.B > 0.9f && b.R < 0.1f, b.ToString());
    }

    [Fact]
    public void Recording_is_cached_by_size_and_by_color_only_when_the_document_uses_current_color()
    {
        var tinted = SvgDocument.Parse(TintedIcon);
        var fixedDoc = SvgDocument.Parse(FixedIcon);
        var color = new State<string>("#ff0000");
        var size = new State<int>(10);
        var other = new State<int>(0);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Style = "color: " + color.Value,
                Children =
                [
                    new Svg(tinted) { Id = "t", Style = "width: " + size.Value + "px; height: 10px" },
                    new Svg(fixedDoc) { Id = "f" },
                    new Div { Id = "other", Style = "height: " + (10 + other.Value) + "px" },
                ],
            },
        });
        var t = (SvgElement)h.El("t");
        var f = (SvgElement)h.El("f");
        Assert.Equal(1, t.Recordings);
        Assert.Equal(1, f.Recordings);

        // 无关的变化：两者都不重录 SVG（甚至元素本身也不重录）
        other.Value = 5;
        h.Frame();
        Assert.Equal(1, t.Recordings);
        Assert.Equal(1, f.Recordings);

        // 颜色变化：只有用到 currentColor 的文档重录
        color.Value = "#0000ff";
        h.Frame();
        Assert.Equal(2, t.Recordings);
        Assert.Equal(1, f.Recordings);

        // 尺寸变化：重录
        size.Value = 20;
        h.Frame();
        Assert.Equal(3, t.Recordings);
    }

    [Fact]
    public void Svg_without_size_attributes_scales_from_its_view_box()
    {
        var doc = SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 10'><rect width='20' height='10' fill='#000000'/></svg>");
        var h = new PipelineHarness(new Div { Children = [new Svg(doc) { Id = "s", Style = "width: 40px" }] });
        Assert.Equal(new Size(40, 20), h.Box("s").Size);
    }

    [Fact]
    public void Icon_is_one_em_square_tinted_and_accessible()
    {
        var doc = SvgDocument.Parse(TintedIcon);
        var h = new PipelineHarness(new Div
        {
            Style = "font-size: 24px; color: #00aa00",
            Children =
            [
                new Icon(doc) { Label = "成功" },
                new Icon(doc) { Size = "12px", Class = "small" },
                new Icon(doc) { Size = "1px; background: red" },
            ],
        });
        var icons = h.Root.RootElement.FindAll(e => e.Tag == "svg").ToList();
        Assert.Equal(3, icons.Count);
        Assert.Equal(new Size(24, 24), icons[0].BoundingRect.Size);
        Assert.Equal(Role.Image, icons[0].Role);
        Assert.Equal("成功", icons[0].AriaLabel);
        Assert.Null(icons[0].GetAttribute("aria-hidden"));
        Assert.Equal(new Size(12, 12), icons[1].BoundingRect.Size);
        Assert.True(icons[1].HasClass("small") && icons[1].HasClass("aw-icon"));
        Assert.Equal("true", icons[1].GetAttribute("aria-hidden"));
        // 不安全的尺寸文本被忽略（不会拼出额外的内联声明）
        Assert.Equal(new Size(24, 24), icons[2].BoundingRect.Size);
        using var bmp = h.Render();
        var px = bmp.GetPixel(12, 12);
        Assert.True(px.G > 0.6f && px.R < 0.1f, px.ToString());
    }

    [Fact]
    public void Icon_rebuild_with_equal_props_is_skipped()
    {
        var doc = SvgDocument.Parse(TintedIcon);
        var tick = new State<int>(0);
        var h = new PipelineHarness(new Lambda { Render = () => new Div { Id = "d" + (tick.Value % 1), Children = [new Icon(doc) { Label = "x" }] } });
        var svg = h.Root.RootElement.ByTag("svg");
        tick.Value++;
        h.Frame();
        Assert.Same(svg, h.Root.RootElement.ByTag("svg"));
    }
}
