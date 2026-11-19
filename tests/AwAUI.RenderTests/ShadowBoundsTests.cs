using AwAUI.Graphics;
using AwAUI.Render.Raster;

namespace AwAUI.RenderTests;

/// <summary>大模糊半径的盒阴影：内存与 σ 无关（低分辨率路径），结果与全分辨率路径一致；超大模糊不会让整数坐标溢出。</summary>
public class ShadowBoundsTests
{
    static Bitmap Render(Action<DrawingContext> draw, int w, int h, float scale = 1)
    {
        var dc = new DrawingContext();
        draw(dc);
        return Golden.Render(dc.Finish(), w, h, scale, Color.White);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Low_resolution_path_matches_full_resolution_path(bool inset)
    {
        void Draw(DrawingContext dc) => dc.DrawBoxShadow(new RoundRect(new Rect(40, 30, 120, 80), 14),
            new BoxShadow(6, 4, 60, inset ? 10 : 4, Color.Parse("#20408080"), inset));
        Bitmap full, low;
        try
        {
            ShadowRasterizer.WorkPixelsOverride = long.MaxValue;
            full = Render(Draw, 200, 140);
            ShadowRasterizer.WorkPixelsOverride = 0;
            low = Render(Draw, 200, 140);
        }
        finally
        {
            ShadowRasterizer.WorkPixelsOverride = null;
        }
        using (full)
        using (low)
        {
            int worst = 0;
            for (int y = 0; y < 140; y++)
            for (int x = 0; x < 200; x++)
            {
                var a = full.GetPixel(x, y);
                var b = low.GetPixel(x, y);
                worst = Math.Max(worst, (int)Math.Round(Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Abs(a.B - b.B)) * 255));
            }
            Assert.True(worst <= 2, $"max channel difference {worst}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_sigma_downsampling_matches_full_resolution(bool inset)
    {
        // σ = 120 设备像素：低分辨率路径的倍数随 σ 增大（k = σ/8 = 15），与全分辨率路径（k = 8）的差别仍在 2/255 内
        void Draw(DrawingContext dc) => dc.DrawBoxShadow(new RoundRect(new Rect(60, 40, 180, 120), 20),
            new BoxShadow(10, 6, 240, inset ? 20 : 0, Color.Parse("#203060"), inset));
        Bitmap full, low;
        try
        {
            ShadowRasterizer.WorkPixelsOverride = long.MaxValue;
            full = Render(Draw, 300, 200);
            ShadowRasterizer.WorkPixelsOverride = 0;
            low = Render(Draw, 300, 200);
        }
        finally
        {
            ShadowRasterizer.WorkPixelsOverride = null;
        }
        using (full)
        using (low)
        {
            int worst = 0;
            for (int y = 0; y < 200; y++)
            for (int x = 0; x < 300; x++)
            {
                var a = full.GetPixel(x, y);
                var b = low.GetPixel(x, y);
                worst = Math.Max(worst, (int)Math.Round(Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Abs(a.B - b.B)) * 255));
            }
            Assert.True(worst <= 2, $"max channel difference {worst}");
        }
    }

    [Theory]
    [InlineData(1_000f, 0f)]
    [InlineData(100_000f, 50_000f)]
    [InlineData(1e9f, 0f)]
    public void Huge_blur_is_bounded_and_does_not_overflow(float blur, float spread)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var b = Render(dc => dc.DrawBoxShadow(new RoundRect(new Rect(50, 50, 300, 300), 0), new BoxShadow(0, 0, blur, spread, Color.Black, false)), 400, 300, 2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64_000_000, $"allocated {allocated / 1e6:F1} MB");
    }
}
