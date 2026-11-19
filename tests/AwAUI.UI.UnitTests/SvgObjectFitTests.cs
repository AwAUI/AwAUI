using AwAUI.Graphics;
using AwAUI.Svg;

namespace AwAUI.UI.UnitTests;

/// <summary>Svg 元素上的 object-fit / object-position / aspect-ratio：与 <c>&lt;img src=svg&gt;</c> 相同的放置规则。</summary>
public class SvgObjectFitTests
{
    // preserveAspectRatio="none"：文档拉伸填满给它的矩形，便于观察 object-fit 求出的矩形
    const string Stretchy = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10' width='10' height='10' preserveAspectRatio='none'>" +
                            "<rect width='10' height='10' fill='#ff0000'/></svg>";

    static bool IsRed(Color c) => c.R > 0.9f && c.G < 0.1f && c.B < 0.1f;

    [Fact]
    public void Initial_fill_stretches_the_document_over_the_content_box()
    {
        var h = new PipelineHarness(new Div { Children = [new Svg(SvgDocument.Parse(Stretchy)) { Id = "s", Style = "display: block; width: 40px; height: 20px" }] },
            defaultSheet: false);
        using var bmp = h.Render();
        Assert.True(IsRed(bmp.GetPixel(2, 10)));
        Assert.True(IsRed(bmp.GetPixel(38, 10)));
    }

    [Fact]
    public void Contain_and_position_place_the_document_rectangle()
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Svg(SvgDocument.Parse(Stretchy)) { Id = "s", Style = "display: block; width: 40px; height: 20px; object-fit: contain; object-position: left" }],
        }, defaultSheet: false);
        using var bmp = h.Render();
        Assert.True(IsRed(bmp.GetPixel(10, 10)));
        ColorAssert.Near(Color.White, bmp.GetPixel(30, 10));
    }

    [Fact]
    public void Cover_is_clipped_to_the_content_box()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "padding: 10px",
            Children = [new Svg(SvgDocument.Parse(Stretchy)) { Id = "s", Style = "display: block; width: 40px; height: 20px; object-fit: cover" }],
        }, defaultSheet: false);
        using var bmp = h.Render();
        Assert.True(IsRed(bmp.GetPixel(30, 20)));
        ColorAssert.Near(Color.White, bmp.GetPixel(30, 5));   // 40×40 的对象矩形向上超出内容盒的部分被裁掉
        ColorAssert.Near(Color.White, bmp.GetPixel(30, 35));
    }

    [Fact]
    public void Size_hints_override_the_document_size_for_object_fit()
    {
        // 提示 20×10 覆盖文档的 10×10：none 按 20×10 居中放进 40×20 的内容盒
        var h = new PipelineHarness(new Div
        {
            Children = [new Svg(SvgDocument.Parse(Stretchy)) { Id = "s", Width = 20, Height = 10, Style = "display: block; width: 40px; height: 20px; object-fit: none" }],
        }, defaultSheet: false);
        using var bmp = h.Render();
        Assert.True(IsRed(bmp.GetPixel(20, 10)));
        Assert.True(IsRed(bmp.GetPixel(11, 6)));
        ColorAssert.Near(Color.White, bmp.GetPixel(5, 10));
        ColorAssert.Near(Color.White, bmp.GetPixel(20, 2));
    }

    [Fact]
    public void Aspect_ratio_overrides_the_view_box_ratio_for_layout()
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Svg(SvgDocument.Parse(Stretchy)) { Id = "s", Style = "width: 30px; aspect-ratio: 3 / 1" }],
        }, "div { display: flex; align-items: flex-start }");
        Assert.Equal(new Size(30, 10), h.Box("s").Size);
    }
}
