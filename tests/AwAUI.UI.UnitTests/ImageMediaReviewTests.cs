using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Svg;

namespace AwAUI.UI.UnitTests;

/// <summary>第七批媒体属性评审中发现的问题的回归测试。</summary>
public class ImageMediaReviewTests
{
    static byte[] Png(int w, int h)
    {
        using var bmp = new Bitmap(new PixelSize(w, h));
        bmp.Clear(Color.Parse("#336699"));
        return AwAUI.Imaging.PngCodec.Encode(bmp);
    }

    [Theory]
    [InlineData("display: block; min-width: 200px", 1000, 100, 50, 200, 100)]
    [InlineData("display: block; max-width: 800px", 300, 1000, 500, 800, 400)]
    [InlineData("display: block; max-width: 100%", 300, 1000, 500, 300, 150)]
    [InlineData("display: block", 300, 1000, 500, 1000, 500)]
    [InlineData("max-width: 100%", 300, 1000, 500, 300, 150)]
    [InlineData("display: block; max-height: 25px", 300, 100, 50, 50, 25)]
    [InlineData("min-width: 200px", 1000, 100, 50, 200, 100)]
    [InlineData("max-height: 25px", 1000, 100, 50, 50, 25)]
    [InlineData("min-height: 100px; max-width: 150px", 1000, 100, 50, 150, 100)]
    [InlineData("max-width: 50%", 300, 1000, 500, 150, 75)]
    [InlineData("display: block; max-width: 40px; max-height: 40px", 300, 100, 50, 40, 20)]
    [InlineData("display: block; min-width: 20px; min-height: 30px", 300, 10, 5, 60, 30)]
    [InlineData("display: block; box-sizing: border-box; padding: 5px; max-height: 35px", 300, 100, 50, 60, 35)]
    [InlineData("display: flex; max-width: 800px", 300, 1000, 500, 800, 400)]
    public void Replaced_min_max_sizes(string style, float container, int nw, int nh, float w, float hgt)
    {
        var h = new PipelineHarness(new Div
        {
            Style = "width: " + container + "px",
            Children = [new Image { Id = "img", Source = ImageSource.FromBytes(Png(nw, nh)), Style = style }],
        }, width: 2000, height: 2000);
        Assert.Equal(new Size(w, hgt), h.Box("img").Size);
    }

    [Theory]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_size_hints_are_ignored_like_invalid_html_attributes(float bad)
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Image { Id = "w", Source = ImageSource.FromBytes(Png(40, 20)), Width = bad },
                new Image { Id = "h", Source = ImageSource.FromBytes(Png(40, 20)), Height = bad, Width = 80 },
            ],
        }, "div { display: flex; align-items: flex-start }");
        Assert.Equal(new Size(40, 20), h.Box("w").Size);
        Assert.Equal(new Size(80, 40), h.Box("h").Size);
    }

    [Fact]
    public void Changing_aspect_ratio_or_display_of_an_image_relayouts()
    {
        var style = new State<string>("aspect-ratio: 1");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Style = "width: 300px",
                Children = [new Image { Id = "img", Source = ImageSource.FromBytes(Png(40, 20)), Style = style.Value }],
            },
        });
        Assert.Equal(new Size(40, 40), h.Box("img").Size);
        style.Value = "aspect-ratio: 4";
        h.Frame();
        Assert.Equal(new Size(40, 10), h.Box("img").Size);
        style.Value = "display: block; margin: 0 auto";
        h.Frame();
        Assert.Equal(new Rect(130, 0, 40, 20), h.Box("img"));
        style.Value = "display: block; width: auto; max-width: 30px";
        h.Frame();
        Assert.Equal(new Rect(0, 0, 30, 15), h.Box("img"));
    }

    /// <summary>3×1（红、绿、蓝）的位图放进 40×40 的内容盒（contain）：目标高 13.33 px、位于 y = 13.33，四边对齐设备像素后边缘不与背景混合。</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void Bitmap_destination_is_snapped_to_device_pixels(float scale)
    {
        using var src = new Bitmap(new PixelSize(3, 1));
        var row = src.GetRow(0);
        byte[] px = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255];
        for (int i = 0; i < px.Length; i++) row[i] = px[i];
        var h = new PipelineHarness(new Div
        {
            Children = [new Image { Id = "img", Source = ImageSource.FromBitmap(src), Style = "display: block; width: 40px; height: 40px; background-color: #000; object-fit: contain; image-rendering: pixelated" }],
        }, width: 60, height: 60, scale: scale, defaultSheet: false);
        using var bmp = h.Render();
        // 对齐后的上下边（设备像素）
        int top = (int)Math.Floor(40f / 3 * scale + 0.5f), bottom = (int)Math.Floor(80f / 3 * scale + 0.5f);
        int x = (int)(5 * scale);
        ColorAssert.Near(Color.Parse("#000000"), bmp.GetPixel(x, top - 1), 0.01f);
        ColorAssert.Near(Color.Parse("#ff0000"), bmp.GetPixel(x, top), 0.01f);
        ColorAssert.Near(Color.Parse("#ff0000"), bmp.GetPixel(x, bottom - 1), 0.01f);
        ColorAssert.Near(Color.Parse("#000000"), bmp.GetPixel(x, bottom), 0.01f);
    }
}
