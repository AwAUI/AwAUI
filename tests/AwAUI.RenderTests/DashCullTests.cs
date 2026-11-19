using AwAUI.Graphics;

namespace AwAUI.RenderTests;

/// <summary>虚线只在可见范围内生成几何：巨大的虚线 / 点线描边耗时与内存有界，可见部分与不裁剪时逐像素一致。</summary>
public class DashCullTests
{
    static Bitmap Render(Action<DrawingContext> draw, int w, int h, float scale = 1)
    {
        var dc = new DrawingContext();
        draw(dc);
        return Golden.Render(dc.Finish(), w, h, scale, Color.Transparent);
    }

    [Fact]
    public void Huge_dotted_round_rect_is_bounded_by_the_visible_area()
    {
        // 10⁶ 逻辑像素的盒子、2x：不裁剪时约 400 万个点（实测 6.6 s、828 MB）
        var shape = new RoundRect(new Rect(0.25f, 0.25f, 1_000_000, 1_000_000), 12);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var b = Render(dc => dc.StrokeRoundRect(shape, Color.Black, new Stroke(0.5f, StrokeCap.Round, dashes: new[] { 0f, 1f })), 200, 100, 2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32_000_000, $"allocated {allocated / 1e6:F1} MB");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
        // 可见的上边与左边仍画出了点
        Assert.True(b.GetPixel(100, 0).A > 0 || b.GetPixel(101, 0).A > 0 || b.GetPixel(102, 0).A > 0);
    }

    [Fact]
    public void Dashes_on_billion_pixel_edges_terminate_and_keep_precision()
    {
        // 10⁹ 量级：float 弧长 pos += 0.5 不再前进（曾死循环），float 插值在可见区附近偏差可达 64 个单位
        var shape = new RoundRect(new Rect(0.5f, 0.5f, 1e9f, 1e9f), 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var b = Render(dc => dc.StrokeRoundRect(shape, Color.Black, new Stroke(1, dashes: new[] { 4f, 4f })), 100, 100);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
        // 上边从起点开始：[0,4) 开、[4,8) 关……；左边是闭合路径的最后一段，它的虚线落在 x≈0.5 处
        Assert.True(b.GetPixel(2, 0).A > 0.2f);
        Assert.True(b.GetPixel(6, 0).A < 0.05f);
        int on = 0;
        for (int y = 10; y < 100; y++) if (b.GetPixel(0, y).A > 0.2f) on++;
        Assert.InRange(on, 30, 60);
    }

    [Theory]
    [InlineData(-37.3f, -21.6f, 3f)]
    [InlineData(5.5f, 40.25f, 1f)]
    [InlineData(-500f, 10f, 2f)]
    public void Culled_dashes_match_uncull_rendering_in_the_visible_region(float x, float y, float width)
    {
        // 同一条路径：在 64×64 的小画布里（大部分被裁掉）与在足够大的画布里（完全可见、不发生裁剪）绘制，重叠区域应一致
        var shape = new RoundRect(new Rect(x, y, 700, 90), 18);
        var stroke = new Stroke(width, StrokeCap.Butt, dashes: new[] { 7f, 5f, 2f, 5f }, dashOffset: 3.5f);
        float ox = (float)Math.Ceiling(Math.Max(0, -x + 10)), oy = (float)Math.Ceiling(Math.Max(0, -y + 10));
        using var small = Render(dc => dc.StrokeRoundRect(shape, Color.Black, stroke), 64, 64);
        using var big = Render(dc =>
        {
            dc.Translate(ox, oy);
            dc.StrokeRoundRect(shape, Color.Black, stroke);
        }, 64 + (int)ox + 800, 64 + (int)oy + 200);
        for (int py = 0; py < 64; py++)
        for (int px = 0; px < 64; px++)
        {
            var a = small.GetPixel(px, py);
            var c = big.GetPixel(px + (int)ox, py + (int)oy);
            Assert.True(Math.Abs(a.A - c.A) <= 2f / 255, $"({px},{py}): {a.A} vs {c.A}");
        }
    }
}
