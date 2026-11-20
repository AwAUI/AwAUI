using System.Numerics;
using System.Runtime.CompilerServices;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.Graphics.UnitTests;

/// <summary>DrawImage 的录制（操作码 34，design/13）与位图的内容版本。</summary>
public class DrawImageTests
{
    static Bitmap Image(int w = 8, int h = 4) => new(new PixelSize(w, h));

    static DrawImageCmd Single(DisplayList list, out Bitmap image)
    {
        var r = list.GetReader();
        Assert.True(r.MoveNext());
        Assert.Equal(DrawOp.DrawImage, r.Op);
        Assert.Equal(DrawFlags.AntiAlias, r.Flags);
        var c = r.Read<DrawImageCmd>();
        image = r.GetImage(c);
        Assert.False(r.MoveNext());
        return c;
    }

    [Fact]
    public void Opcode_and_payload_layout_are_stable()
    {
        Assert.Equal(34, (int)DrawOp.DrawImage);
        Assert.Equal(0, (int)ImageSampling.Nearest);
        Assert.Equal(1, (int)ImageSampling.Linear);
        // int + 2 × Rect + float + 采样方式（1 字节）+ 3 字节填充
        Assert.Equal(44, Unsafe.SizeOf<DrawImageCmd>());
        // DrawImage 没有改变格式版本；第九批的滤镜图层与 DrawBackdrop 把它升为 2
        Assert.Equal(2, DisplayList.FormatVersion);
    }

    [Fact]
    public void Records_source_destination_sampling_and_opacity()
    {
        using var img = Image();
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(1, 1, 4, 2), new Rect(10, 20, 40, 30), ImageSampling.Nearest, 0.25f);
        var list = dc.Finish();
        var c = Single(list, out var resource);
        Assert.Same(img, resource);
        Assert.Equal(new Rect(1, 1, 4, 2), c.Src);
        Assert.Equal(new Rect(10, 20, 40, 30), c.Dst);
        Assert.Equal(ImageSampling.Nearest, c.Sampling);
        Assert.Equal(0.25f, c.Opacity);
        Assert.Equal(new Rect(10, 20, 40, 30), list.Bounds);
    }

    [Fact]
    public void Whole_image_overload_uses_the_full_source_rect()
    {
        using var img = Image(8, 4);
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 16, 8));
        var c = Single(dc.Finish(), out _);
        Assert.Equal(new Rect(0, 0, 8, 4), c.Src);
        Assert.Equal(ImageSampling.Linear, c.Sampling);
        Assert.Equal(1f, c.Opacity);
    }

    [Fact]
    public void Source_outside_the_bitmap_is_clipped_and_destination_shrinks_proportionally()
    {
        // Canvas 2D 的 drawImage 规则：源矩形 (-4, 0, 16, 4) 只有 (0, 0, 8, 4) 在位图内，目标按 2 倍缩放同样收缩
        using var img = Image(8, 4);
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(-4, 0, 16, 4), new Rect(0, 0, 32, 8));
        var c = Single(dc.Finish(), out _);
        Assert.Equal(new Rect(0, 0, 8, 4), c.Src);
        Assert.Equal(new Rect(8, 0, 16, 8), c.Dst);
    }

    [Fact]
    public void Invalid_commands_are_dropped_at_record_time()
    {
        using var img = Image();
        var disposed = Image();
        disposed.Dispose();
        var dc = new DrawingContext();
        dc.DrawImage(disposed, new Rect(0, 0, 4, 4));
        dc.DrawImage(img, new Rect(0, 0, 0, 4));
        dc.DrawImage(img, new Rect(0, 0, 4, 4), new Rect(0, 0, 4, -4));
        dc.DrawImage(img, new Rect(float.NaN, 0, 4, 4), new Rect(0, 0, 4, 4));
        dc.DrawImage(img, new Rect(0, 0, 4, 4), new Rect(0, 0, float.PositiveInfinity, 4));
        dc.DrawImage(img, new Rect(100, 100, 4, 4), new Rect(0, 0, 4, 4));
        dc.DrawImage(img, new Rect(0, 0, 4, 4), ImageSampling.Linear, 0);
        dc.DrawImage(img, new Rect(0, 0, 4, 4), ImageSampling.Linear, float.NaN);
        // 极端比例：裁剪后目标溢出为无穷大
        dc.DrawImage(img, new Rect(-1e30f, 0, 1e30f + 1, 4), new Rect(0, 0, float.MaxValue, 4));
        Assert.Equal(0, dc.Finish().CommandCount);
        Assert.Throws<ArgumentNullException>(() => new DrawingContext().DrawImage(null!, new Rect(0, 0, 1, 1)));
    }

    [Fact]
    public void Opacity_above_one_and_unknown_sampling_are_normalised()
    {
        using var img = Image();
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 4, 4), (ImageSampling)9, 3);
        var c = Single(dc.Finish(), out _);
        Assert.Equal(1f, c.Opacity);
        Assert.Equal(ImageSampling.Linear, c.Sampling);
    }

    [Fact]
    public void Bounds_follow_transform_and_clip()
    {
        using var img = Image();
        var dc = new DrawingContext();
        dc.Transform(Matrix3x2.CreateScale(2));
        dc.ClipRect(new Rect(0, 0, 5, 5));
        dc.DrawImage(img, new Rect(0, 0, 20, 20));
        Assert.Equal(new Rect(0, 0, 10, 10), dc.Finish().Bounds);
    }

    [Fact]
    public void Same_bitmap_is_one_resource()
    {
        using var img = Image();
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 4, 4));
        dc.DrawImage(img, new Rect(4, 0, 4, 4));
        Assert.Single(dc.Finish().Resources);
    }

    [Fact]
    public void Writers_bump_the_bitmap_version()
    {
        using var img = Image();
        long v0 = img.Version;
        img.Clear(Color.Black);
        long v1 = img.Version;
        Assert.True(v1 > v0);
        img.Invalidate();
        Assert.True(img.Version > v1);
        using var other = Image();
        long v2 = other.Version;
        img.CopyTo(other);
        Assert.True(other.Version > v2);
        Assert.False(img.IsDisposed);
    }

    [Fact]
    public void Content_hash_includes_the_bitmap_version_at_record_time()
    {
        using var img = Image();
        DisplayList Record()
        {
            var dc = new DrawingContext();
            dc.DrawImage(img, new Rect(0, 0, 4, 4));
            return dc.Finish();
        }
        var a = Record();
        var b = Record();
        Assert.Equal(a.ContentHash, b.ContentHash);
        img.Invalidate();
        Assert.NotEqual(a.ContentHash, Record().ContentHash);
    }
}
