using AwAUI.Graphics;
using AwAUI.Render.Gpu.Atlas;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>图集装箱、按页淘汰与渐变查找表（与平台无关，不需要 GPU）。</summary>
public class AtlasTests
{
    static bool Overlap((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    [Fact]
    public void Skyline_packs_without_overlap_and_within_bounds()
    {
        var packer = new SkylinePacker(256, 256);
        var rng = new Random(7);
        var placed = new List<(int X, int Y, int W, int H)>();
        long area = 0;
        while (true)
        {
            int w = 1 + rng.Next(24), h = 1 + rng.Next(24);
            if (!packer.TryPack(w, h, out int x, out int y)) break;
            Assert.InRange(x, 0, 256 - w);
            Assert.InRange(y, 0, 256 - h);
            var r = (x, y, w, h);
            foreach (var p in placed) Assert.False(Overlap(p, r), $"{r} overlaps {p}");
            placed.Add(r);
            area += w * h;
        }
        Assert.Equal(area, packer.UsedArea);
        // 天际线装箱对相近大小的矩形利用率应当很高
        Assert.True(area > 256 * 256 * 0.7, $"utilisation {area / (256.0 * 256):P0}");
        Assert.True(packer.SegmentCount <= 256);
    }

    [Fact]
    public void Skyline_rejects_oversized_and_reset_frees_space()
    {
        var packer = new SkylinePacker(64, 32);
        Assert.False(packer.TryPack(65, 1, out _, out _));
        Assert.False(packer.TryPack(1, 33, out _, out _));
        Assert.False(packer.TryPack(0, 5, out _, out _));
        Assert.True(packer.TryPack(64, 32, out int x, out int y));
        Assert.Equal((0, 0), (x, y));
        Assert.False(packer.TryPack(1, 1, out _, out _));
        packer.Reset();
        Assert.True(packer.TryPack(64, 32, out _, out _));
    }

    [Fact]
    public void Skyline_prefers_lowest_position()
    {
        var packer = new SkylinePacker(100, 100);
        Assert.True(packer.TryPack(60, 50, out _, out _));
        Assert.True(packer.TryPack(40, 10, out int x, out int y));
        Assert.Equal((60, 0), (x, y));
        Assert.True(packer.TryPack(40, 10, out x, out y));
        Assert.Equal((60, 10), (x, y));
        // 现在最低的是 (60, 20)，宽 40
        Assert.True(packer.TryPack(50, 5, out x, out y));
        Assert.Equal((0, 50), (x, y));
    }

    [Fact]
    public void Atlas_grows_pages_up_to_limit_then_evicts_least_recently_used()
    {
        var atlas = new MaskAtlas(null, pageSize: 64, maxPages: 2);
        atlas.BeginFrame(1);
        Assert.True(atlas.TryAllocate(64, 64, out var a));      // 第 1 页
        atlas.BeginFrame(2);
        Assert.True(atlas.TryAllocate(64, 64, out var b));      // 第 2 页
        Assert.Equal(2, atlas.Pages.Count);
        Assert.NotSame(a.Page, b.Page);

        atlas.BeginFrame(3);
        b.Touch(3);                                             // 第 2 页本帧在用
        Assert.True(atlas.TryAllocate(32, 32, out var c));      // 淘汰最久未用的第 1 页
        Assert.Same(a.Page, c.Page);
        Assert.False(a.IsValid);
        Assert.True(b.IsValid);
        Assert.True(c.IsValid);
        Assert.Equal(1, atlas.Evictions);
    }

    [Fact]
    public void Atlas_never_evicts_pages_used_in_the_current_frame()
    {
        var atlas = new MaskAtlas(null, pageSize: 32, maxPages: 1, frameOverflowBudget: 32 * 32 * 3);
        atlas.BeginFrame(1);
        var slots = new List<AtlasSlot>();
        for (int i = 0; i < 4; i++)
        {
            Assert.True(atlas.TryAllocate(32, 32, out var s));
            slots.Add(s);
        }
        // 4 页都是本帧分配的：超出上限的 3 页计入预算，没有任何条目失效
        Assert.All(slots, s => Assert.True(s.IsValid));
        Assert.Equal(4, atlas.Pages.Count);
        Assert.False(atlas.TryAllocate(32, 32, out _));         // 预算用完

        atlas.BeginFrame(2);                                    // 回收到上限
        Assert.Single(atlas.Pages);
        Assert.True(atlas.TryAllocate(16, 16, out _));
    }

    [Fact]
    public void Oversized_masks_get_transient_pages_within_budget()
    {
        var atlas = new MaskAtlas(null, pageSize: 64, maxPages: 2, frameOverflowBudget: 200 * 200);
        atlas.BeginFrame(1);
        Assert.True(atlas.TryAllocate(100, 30, out var big));
        Assert.True(big.Page.IsTransient);
        Assert.Equal(1, atlas.TransientPageCount);
        Assert.False(atlas.TryAllocate(300, 300, out _));
        atlas.EndFrame();
        Assert.Equal(0, atlas.TransientPageCount);
        Assert.False(big.IsValid);
    }

    [Fact]
    public void Atlas_tracks_dirty_region_for_upload()
    {
        var atlas = new MaskAtlas(null, pageSize: 64, maxPages: 1);
        atlas.BeginFrame(1);
        Assert.True(atlas.TryAllocate(10, 5, out var a));
        Assert.True(atlas.TryAllocate(7, 9, out var b));
        a.Commit();
        b.Commit();
        var dirty = a.Page.Dirty;
        Assert.True(dirty.Width >= 17 || dirty.Height >= 14);
        Assert.Equal(new PixelRect(a.X, a.Y, a.Width, a.Height).Union(new PixelRect(b.X, b.Y, b.Width, b.Height)), dirty);
    }

    [Fact]
    public void Gradient_luts_are_shared_by_content_and_reused_lru()
    {
        var luts = new GradientLuts(null);
        luts.BeginFrame(1);
        var stops = new[] { new GradientStop(0, Color.Parse("#ff0000")), new GradientStop(1, Color.Parse("#0000ff80")) };
        var a = new LinearGradientBrush(new Point(0, 0), new Point(1, 0), stops);
        var b = new RadialGradientBrush(new Point(0, 0), 3, (GradientStop[])stops.Clone());
        Assert.True(luts.TryGet(a, out var pa, out int ra));
        Assert.True(luts.TryGet(b, out var pb, out int rb));
        Assert.Same(pa, pb);
        Assert.Equal(ra, rb);
        Assert.Equal(1, luts.CachedCount);

        // 填满一页的全部行，下一帧再分配时复用最久未用的行
        for (int i = 0; i < GradientLuts.RowsPerPage - 1; i++)
            Assert.True(luts.TryGet(new LinearGradientBrush(new Point(0, 0), new Point(1, 0), new[] { new GradientStop(0, new Color(i / 255f, 0, 0)), new GradientStop(1, Color.White) }), out _, out _));
        Assert.Equal(1, luts.PageCount);
        Assert.True(luts.TryGet(new LinearGradientBrush(new Point(0, 0), new Point(1, 0), new[] { new GradientStop(0, Color.Black), new GradientStop(1, Color.Black) }), out _, out _));
        Assert.Equal(2, luts.PageCount);   // 本帧的行都在用：开新页

        luts.BeginFrame(2);
        Assert.True(luts.TryGet(a, out _, out _));
        Assert.True(luts.TryGet(new LinearGradientBrush(new Point(0, 0), new Point(1, 0), new[] { new GradientStop(0, Color.White), new GradientStop(1, Color.Black) }), out _, out _));
        Assert.Equal(2, luts.PageCount);
    }

    [Fact]
    public void Gradient_lut_pages_are_capped_per_frame()
    {
        var luts = new GradientLuts(null);
        luts.BeginFrame(1);
        int ok = 0;
        for (int i = 0; i < (GradientLuts.MaxPages + 1) * GradientLuts.RowsPerPage; i++)
        {
            var g = new LinearGradientBrush(new Point(0, 0), new Point(1, 0), new[] { new GradientStop(0, Color.Black), new GradientStop(i / 1e6f, Color.White) });
            if (luts.TryGet(g, out _, out _)) ok++;
        }
        Assert.Equal(GradientLuts.MaxPages * GradientLuts.RowsPerPage, ok);
        Assert.Equal(GradientLuts.MaxPages, luts.PageCount);
        // 下一帧可以复用上一帧的行
        luts.BeginFrame(2);
        Assert.True(luts.TryGet(new LinearGradientBrush(new Point(0, 0), new Point(1, 0), new[] { new GradientStop(0, Color.White), new GradientStop(1, Color.White) }), out _, out _));
    }

    [Fact]
    public void Gradient_lut_sampling_matches_css_premultiplied_interpolation()
    {
        var stops = new[] { new GradientStop(0.2f, new Color(1, 0, 0, 1)), new GradientStop(0.8f, new Color(0, 0, 1, 0)) };
        Assert.Equal(new System.Numerics.Vector4(1, 0, 0, 1), GradientLuts.Sample(stops, 0));
        Assert.Equal(new System.Numerics.Vector4(0, 0, 0, 0), GradientLuts.Sample(stops, 1));
        var mid = GradientLuts.Sample(stops, 0.5f);
        Assert.Equal(0.5f, mid.X, 3);
        Assert.Equal(0f, mid.Z, 3);   // 预乘插值：透明端不引入蓝色
        Assert.Equal(0.5f, mid.W, 3);
    }

    /// <summary>
    /// 回归：色标偏移或颜色为 NaN 时（GradientBrush 的单调修正 Math.Max 会把 NaN 传给后面所有色标），按 == 比较的键永远不等于自己：
    /// 每次绘制都新占一行，LRU 复用时 Remove(旧键) 失败，字典每帧多一项、无界增长。
    /// </summary>
    [Fact]
    public void Gradient_luts_with_nan_stops_stay_bounded_across_frames()
    {
        var luts = new GradientLuts(null);
        var nanOffset = new LinearGradientBrush(new Point(0, 0), new Point(1, 0), new[] { new GradientStop(float.NaN, Color.Black), new GradientStop(1, Color.White) });
        var nanColor = new RadialGradientBrush(new Point(0, 0), 5, new[] { new GradientStop(0, new Color(float.NaN, 0, 0, 1)), new GradientStop(1, Color.White) });
        for (int frame = 1; frame <= 2000; frame++)
        {
            luts.BeginFrame(frame);
            Assert.True(luts.TryGet(nanOffset, out _, out int row1));
            Assert.True(luts.TryGet(nanColor, out _, out int row2));
            Assert.NotEqual(row1, row2);
        }
        Assert.Equal(2, luts.CachedCount);
        Assert.Equal(1, luts.PageCount);
    }

    /// <summary>回归：含 NaN 坐标的路径掩码键也必须等于自己，否则缓存命中不了、整理时也删不掉（缓存被永久占满）。</summary>
    [Fact]
    public void Path_mask_keys_with_nan_coordinates_equal_themselves()
    {
        var path = new PathBuilder().MoveTo(0, 0).LineTo(float.NaN, 5).LineTo(5, 5).Close().ToPath();
        var stroke = new Stroke(float.NaN);
        var a = new PathMaskKey(path, stroke, System.Numerics.Matrix3x2.Identity, new PixelRect(0, 0, 8, 8), true);
        var copy = new PathBuilder().MoveTo(0, 0).LineTo(float.NaN, 5).LineTo(5, 5).Close().ToPath();
        var b = new PathMaskKey(copy, new Stroke(float.NaN), System.Numerics.Matrix3x2.Identity, new PixelRect(0, 0, 8, 8), true);
        Assert.True(a.Equals(a));
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var cache = new PathMaskCache(16);
        var atlas = new MaskAtlas(null, 64, 1);
        atlas.BeginFrame(1);
        Assert.True(atlas.TryAllocate(4, 4, out var slot));
        cache.Add(a, slot, 1);
        Assert.True(cache.TryGet(b, 2, out _));
        Assert.Equal(1, cache.Count);
    }
}
