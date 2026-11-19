using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 替换元素经 <see cref="LayoutMapper.ForReplaced"/> 标记 <c>LayoutStyle.IsReplaced</c>（取代「display 映射为 InlineFlex」的规避做法），
/// 测量函数不再猜测确定的约束宽度是可用宽度还是钳制结果：百分比 min / max、绝对定位与网格对齐都由布局引擎按替换元素处理。
/// </summary>
public class ReplacedLayoutFlagTests
{
    static byte[] Png(int w, int h)
    {
        using var bmp = new Bitmap(new PixelSize(w, h));
        bmp.Clear(Color.Parse("#336699"));
        return AwAUI.Imaging.PngCodec.Encode(bmp);
    }

    static Image Img(string style) => new() { Id = "img", Source = ImageSource.FromBytes(Png(50, 25)), Style = style };

    /// <summary>roadmap 的例子：1000px 容器里 50×25 的图片带 min-width:10%，得到 100×50（原来测量函数把可用宽度当作钳制结果，得到 1000×500）。</summary>
    [Theory]
    [InlineData("min-width: 10%")]
    [InlineData("display: block; min-width: 10%")]
    [InlineData("display: flex; min-width: 10%")]
    public void Percentage_min_width_scales_the_image_by_its_ratio(string style)
    {
        var h = new PipelineHarness(new Div { Style = "width: 1000px", Children = [Img(style)] }, width: 2000, height: 2000);
        Assert.Equal(new Size(100, 50), h.Box("img").Size);
    }

    [Fact]
    public void Block_level_image_is_not_stretched_to_the_container_width()
    {
        var h = new PipelineHarness(new Div { Style = "width: 400px", Children = [Img("display: block")] });
        Assert.Equal(new Rect(0, 0, 50, 25), h.Box("img"));
    }

    [Theory]
    [InlineData("position: absolute; left: 10px; right: 10px; top: 0")]
    [InlineData("position: absolute; left: 10px; right: 10px; top: 0; bottom: 0")]
    public void Absolutely_positioned_image_with_both_insets_keeps_its_natural_size(string style)
    {
        // CSS 2.1 §10.3.8 / §10.6.5：替换元素的 auto 尺寸取固有尺寸，不随 left + right（top + bottom）拉伸
        var h = new PipelineHarness(new Div { Style = "position: relative; width: 400px; height: 300px", Children = [Img(style)] });
        Assert.Equal(new Rect(10, 0, 50, 25), h.Box("img"));
    }

    [Fact]
    public void Images_in_a_grid_keep_their_natural_size_while_non_replaced_ratio_items_fill_the_column()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "display: grid; grid-template-columns: 300px; grid-auto-rows: 200px",
            Children =
            [
                Img(""),
                new Div { Id = "card", Style = "aspect-ratio: 2" },
            ],
        }, width: 400, height: 500);
        // normal 对齐：替换元素两轴按 start，保持 50×25
        Assert.Equal(new Rect(0, 0, 50, 25), h.Box("img"));
        // 非替换的 aspect-ratio 项目在行内轴撑满，高度由比例推出（Grid §6.2）
        Assert.Equal(new Rect(0, 200, 300, 150), h.Box("card"));
    }

    [Fact]
    public void Height_given_by_css_derives_the_width_from_the_ratio()
    {
        // 测量函数不再由确定的约束高度推宽度：交给引擎按宽高比推出
        var h = new PipelineHarness(new Div { Style = "width: 400px", Children = [Img("display: block; height: 50px")] });
        Assert.Equal(new Size(100, 50), h.Box("img").Size);
    }
}
