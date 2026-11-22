using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Render.Raster;

namespace AwAUI.RenderTests;

/// <summary>
/// 盒阴影蒙版缓存（design/01「内存规则」）：相同几何的阴影复用上次的结果，裁剪后的子区域也能命中，
/// 且命中的结果与重新计算逐位相同；缓存总量有上限。
/// </summary>
public class ShadowCacheTests
{
    static readonly PixelRect Window = new(0, 0, 400, 300);

    static BoxShadowCmd Shadow(float blur = 30, bool inset = false) => new()
    {
        Shape = new RoundRect(new Rect(80, 60, 200, 120), 16),
        OffsetX = 0, OffsetY = 10, Blur = blur, Spread = 0,
        Color = Color.Parse("#1e293b"),
        Inset = inset ? 1 : 0,
    };

    static ShadowRasterizer Create() => new(new Rasterizer());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_shadow_reuses_the_cached_mask(bool inset)
    {
        var shadows = Create();
        var cmd = Shadow(inset: inset);
        Assert.True(shadows.Compute(cmd, Matrix3x2.Identity, Window, Window, out var first, out var rect1, out var out1));
        Assert.True(shadows.Compute(cmd, Matrix3x2.Identity, Window, Window, out var second, out var rect2, out var out2));
        Assert.Same(first, second);
        Assert.Equal(rect1, rect2);
        Assert.Equal(out1, out2);
        Assert.Equal(1, shadows.CacheHits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clipped_request_hits_and_matches_a_fresh_computation(bool inset)
    {
        var cmd = Shadow(inset: inset);
        var clip = new PixelRect(60, 40, 90, 70);   // 局部重画：只要阴影的左上角

        var cached = Create();
        Assert.True(cached.Compute(cmd, Matrix3x2.Identity, Window, Window, out _, out _, out _));
        Assert.True(cached.Compute(cmd, Matrix3x2.Identity, clip, Window, out var hit, out var hitRect, out var hitOut));
        Assert.Equal(1, cached.CacheHits);

        Assert.True(Create().Compute(cmd, Matrix3x2.Identity, clip, Window, out var fresh, out var freshRect, out var freshOut));
        Assert.Equal(freshOut, hitOut);
        for (int y = hitOut.Y; y < hitOut.Bottom; y++)
        for (int x = hitOut.X; x < hitOut.Right; x++)
        {
            float a = hit[(y - hitRect.Y) * hitRect.Width + (x - hitRect.X)];
            float b = fresh[(y - freshRect.Y) * freshRect.Width + (x - freshRect.X)];
            Assert.True(a == b, $"({x},{y}): cached {a} vs fresh {b}");
        }
    }

    [Fact]
    public void Larger_request_and_different_geometry_miss()
    {
        var shadows = Create();
        var clip = new PixelRect(60, 40, 90, 70);
        Assert.True(shadows.Compute(Shadow(), Matrix3x2.Identity, clip, Window, out var small, out _, out _));
        // 缓存的只是一个角：整个阴影要重新计算，并取代旧项
        Assert.True(shadows.Compute(Shadow(), Matrix3x2.Identity, Window, Window, out var full, out _, out _));
        Assert.NotSame(small, full);
        Assert.Equal(0, shadows.CacheHits);
        Assert.Equal(full.Length, shadows.CachedFloats);

        Assert.True(shadows.Compute(Shadow(blur: 12), Matrix3x2.Identity, Window, Window, out var other, out _, out _));
        Assert.NotSame(full, other);
        Assert.True(shadows.Compute(Shadow(), Matrix3x2.CreateScale(2), Window, Window, out var scaled, out _, out _));
        Assert.NotSame(full, scaled);
        Assert.Equal(0, shadows.CacheHits);
    }

    [Fact]
    public void Cache_stays_within_its_budget()
    {
        var shadows = Create();
        for (int i = 0; i < 40; i++)
        {
            Assert.True(shadows.Compute(Shadow(blur: 10 + i), Matrix3x2.Identity, Window, Window, out _, out _, out _));
            Assert.InRange(shadows.CachedFloats, 0, ShadowRasterizer.MaxCachedFloats);
        }
    }
}
