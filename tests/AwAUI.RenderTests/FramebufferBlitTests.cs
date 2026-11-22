using AwAUI.Graphics;
using AwAUI.Render.Raster;

namespace AwAUI.RenderTests;

/// <summary>
/// 纯滚动快路径的帧间搬移（<see cref="FramebufferBlit"/>）：源与目的可以重叠、可以越出位图边缘，
/// 结果必须与「从原始快照按 memmove 语义复制」逐字节一致。
/// </summary>
public class FramebufferBlitTests
{
    const int W = 9, H = 7;

    static Bitmap Original()
    {
        var b = new Bitmap(new PixelSize(W, H));
        for (int y = 0; y < H; y++)
        {
            var row = b.GetRow(y);
            for (int x = 0; x < W; x++)
            {
                int o = x * 4;
                row[o] = (byte)(x * 23 + 7);
                row[o + 1] = (byte)(y * 31 + 11);
                row[o + 2] = (byte)(x * 17 + y * 13);
                row[o + 3] = (byte)(200 + ((x + y) % 56));
            }
        }
        return b;
    }

    static byte[] Snapshot(Bitmap b)
    {
        var data = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            b.GetRow(y).Slice(0, W * 4).CopyTo(data.AsSpan(y * W * 4, W * 4));
        return data;
    }

    /// <summary>参考实现：证 SHIFT 相同语义的搬移，从原始快照直接写目的。</summary>
    static (byte[] Pixels, long Count) Expected(byte[] original, PixelRect rect, int dx, int dy)
    {
        var result = (byte[])original.Clone();
        var full = new PixelRect(0, 0, W, H);
        var source = rect.Intersect(full);
        if (source.IsEmpty || (dx == 0 && dy == 0)) return (result, 0);
        var dest = new PixelRect(source.X + dx, source.Y + dy, source.Width, source.Height).Intersect(full);
        if (dest.IsEmpty) return (result, 0);
        source = new PixelRect(dest.X - dx, dest.Y - dy, dest.Width, dest.Height).Intersect(source);
        if (source.IsEmpty) return (result, 0);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                int s = ((source.Y + y) * W + source.X + x) * 4;
                int d = ((source.Y + dy + y) * W + source.X + dx + x) * 4;
                Array.Copy(original, s, result, d, 4);
            }
        return (result, (long)source.Width * source.Height);
    }

    [Theory]
    [InlineData(1, 0, 1, 0)]    // 向右 1：行内重叠，必须 memmove
    [InlineData(-1, 0, 2, 0)]   // 向左 1
    [InlineData(0, 1, 2, 3)]    // 向下 1：行间重叠
    [InlineData(0, -2, 2, 4)]   // 向上 2
    [InlineData(3, 2, 1, 1)]    // 双向位移
    [InlineData(-4, -3, 0, 0)]  // 双向负位移
    [InlineData(2, 0, 0, 0)]    // 超出源矩形宽度的水平位移（源与目的不重叠）
    [InlineData(0, 5, 0, 2)]
    public void Shift_matches_memmove_from_the_original_snapshot(int dx, int dy, int rx, int ry)
    {
        var rect = new PixelRect(rx, ry, 5, 4);
        using var bmp = Original();
        var original = Snapshot(bmp);
        var (expected, count) = Expected(original, rect, dx, dy);
        long version = bmp.Version;

        long moved = FramebufferBlit.Shift(bmp, new FramebufferShift(rect, dx, dy));

        Assert.Equal(count, moved);
        Assert.Equal(expected, Snapshot(bmp));
        if (count > 0) Assert.NotEqual(version, bmp.Version);
    }

    [Theory]
    [InlineData(0, 0, 4, 4, 0, 0)]     // 无位移：不搬移
    [InlineData(50, 0, 4, 4, 3, 0)]    // 源完全在目标之外
    [InlineData(0, 0, 4, 4, 20, 0)]    // 目的完全在目标之外
    [InlineData(-6, 0, 4, 4, 3, 0)]    // 源部分越出左边界
    [InlineData(7, 6, 4, 4, 5, 5)]     // 源部分越出右下边界
    public void Shift_handles_empty_and_out_of_bounds_inputs(int rx, int ry, int w, int h, int dx, int dy)
    {
        var rect = new PixelRect(rx, ry, w, h);
        using var bmp = Original();
        var original = Snapshot(bmp);
        var (expected, count) = Expected(original, rect, dx, dy);
        long version = bmp.Version;

        long moved = FramebufferBlit.Shift(bmp, new FramebufferShift(rect, dx, dy));

        Assert.Equal(count, moved);
        Assert.Equal(expected, Snapshot(bmp));
        if (count == 0) Assert.Equal(version, bmp.Version);
    }

    [Fact]
    public void Shift_clips_the_destination_to_the_target_and_returns_the_copied_pixel_count()
    {
        // 源 [0,0,5x4] 向右 7：目的 x=7 宽 5，只有 2 列可见 → 复制 2x4 = 8 像素
        using var bmp = Original();
        var original = Snapshot(bmp);
        var (expected, count) = Expected(original, new PixelRect(0, 0, 5, 4), 7, 0);

        long moved = FramebufferBlit.Shift(bmp, new FramebufferShift(new PixelRect(0, 0, 5, 4), 7, 0));

        Assert.Equal(8, count);
        Assert.Equal(8, moved);
        Assert.Equal(expected, Snapshot(bmp));
        // 可见的两列与源的前两列相同
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 2; x++)
                Assert.Equal(original.AsSpan((y * W + x) * 4, 4).ToArray(), Snapshot(bmp).AsSpan((y * W + 7 + x) * 4, 4).ToArray());
    }
}
