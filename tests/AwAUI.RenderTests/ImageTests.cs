using System.Diagnostics;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Render.Raster;

namespace AwAUI.RenderTests;

/// <summary>DrawImage（操作码 34）的软件后端：采样语义、源矩形不渗色、mip 缩小、内容版本、恶意输入。</summary>
public class ImageTests
{
    public static IEnumerable<object[]> SceneNames => ImageScenes.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Image_scene_matches_golden(string name)
    {
        var scene = ImageScenes.All.Single(s => s.Name == name);
        var dc = new DrawingContext();
        scene.Draw(dc);
        using var bmp = Golden.Render(dc.Finish(), scene.Width, scene.Height);
        Golden.AssertMatches(name, bmp);
    }

    [Fact]
    public void Image_scene_at_2x_matches_golden()
    {
        var dc = new DrawingContext();
        ImageScenes.Sampling(dc);
        using var bmp = Golden.Render(dc.Finish(), 320, 200, 2);
        Golden.AssertMatches("image-sampling@2x", bmp);
    }

    static Bitmap Render(Action<DrawingContext> draw, int w, int h, double scale = 1, List<string>? degraded = null, Color? clear = null)
    {
        var dc = new DrawingContext();
        draw(dc);
        using var backend = new SoftwareRenderBackend();
        if (degraded != null) backend.Degraded += degraded.Add;
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize((int)(w * scale), (int)(h * scale)), scale);
        target.Render(dc.Finish(), new RenderOptions(clear ?? Color.Transparent));
        var copy = new Bitmap(target.PixelSize);
        target.Bitmap.CopyTo(copy);
        return copy;
    }

    static byte[] Pixel(Bitmap b, int x, int y) => b.GetRow(y).Slice(x * 4, 4).ToArray();

    [Theory]
    [InlineData(ImageSampling.Nearest)]
    [InlineData(ImageSampling.Linear)]
    public void Pixel_aligned_unscaled_draw_copies_texels_exactly(ImageSampling sampling)
    {
        using var img = TestImages.Checker(9, 7);
        using var bmp = Render(dc => dc.DrawImage(img, new Rect(3, 2, 9, 7), sampling), 16, 12);
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 9; x++)
                Assert.Equal(Pixel(img, x, y), Pixel(bmp, x + 3, y + 2));
        Assert.Equal(new byte[4], Pixel(bmp, 2, 2));
        Assert.Equal(new byte[4], Pixel(bmp, 12, 8));
    }

    [Fact]
    public void Nearest_upscale_repeats_each_texel_as_a_block()
    {
        using var img = TestImages.Checker(5, 4);
        using var bmp = Render(dc => dc.DrawImage(img, new Rect(0, 0, 15, 12), ImageSampling.Nearest), 15, 12);
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 15; x++)
                Assert.Equal(Pixel(img, x / 3, y / 3), Pixel(bmp, x, y));
    }

    [Fact]
    public void Linear_upscale_interpolates_between_texel_centres()
    {
        // 2×1 的黑白位图放大到 4×1：像素中心 0.5、1.5、2.5、3.5 对应纹素坐标 0.25、0.75、1.25、1.75，
        // 双线性（纹素中心在 0.5、1.5，两端钳制）得 0、0.25、0.75、1
        using var img = Bitmap.FromRgbaUnpremultiplied(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, new PixelSize(2, 1));
        using var bmp = Render(dc => dc.DrawImage(img, new Rect(0, 0, 4, 1)), 4, 1);
        Assert.Equal(new byte[] { 0, 64, 191, 255 }, Enumerable.Range(0, 4).Select(x => Pixel(bmp, x, 0)[0]).ToArray());
    }

    [Theory]
    [InlineData(ImageSampling.Linear, 1f)]
    [InlineData(ImageSampling.Nearest, 1f)]
    [InlineData(ImageSampling.Linear, 0.3f)]      // 缩小：第 L 级由裁剪区构造
    [InlineData(ImageSampling.Linear, 0.05f)]
    public void Source_rect_does_not_bleed_neighbouring_texels(ImageSampling sampling, float rotation)
    {
        using var img = TestImages.Quadrants(8);
        var red = Pixel(img, 0, 0);
        foreach (var (dst, rot) in new[] { (new Rect(4, 4, 92, 92), 0f), (new Rect(10, 10, 3, 3), 0f), (new Rect(20, 20, 60, 60), rotation) })
        {
            using var bmp = Render(dc =>
            {
                dc.Transform(Matrix3x2.CreateRotation(rot, new Vector2(50, 50)));
                dc.DrawImage(img, new Rect(0, 0, 8, 8), dst, sampling);
            }, 100, 100);
            // 只看完全被覆盖的像素（边缘是抗锯齿的覆盖率）：颜色必须恰好是红色象限
            var inner = dst.Inflate(-2);
            for (int y = 0; y < 100; y++)
                for (int x = 0; x < 100; x++)
                {
                    var local = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), Matrix3x2.CreateRotation(-rot, new Vector2(50, 50)));
                    if (!inner.Contains(new Point(local.X, local.Y))) continue;
                    Assert.Equal(red, Pixel(bmp, x, y));
                }
        }
    }

    [Fact]
    public void Opacity_scales_premultiplied_colour()
    {
        using var img = TestImages.Quadrants(2);
        using var bmp = Render(dc => dc.DrawImage(img, new Rect(0, 0, 2, 2), new Rect(0, 0, 4, 4), ImageSampling.Nearest, 0.5f), 4, 4);
        var full = Pixel(img, 0, 0);
        var half = Pixel(bmp, 1, 1);
        for (int c = 0; c < 4; c++) Assert.InRange(half[c], full[c] / 2 - 1, full[c] / 2 + 1);
    }

    [Fact]
    public void Downscaled_stripes_are_prefiltered_not_aliased()
    {
        // 1 像素宽的黑白竖线缩小 8 倍：双线性（mip）得到均匀的中灰；最近邻按定义取到同一相位的线（全黑或全白）
        using var img = TestImages.Stripes(256, 64);
        using var linear = Render(dc => dc.DrawImage(img, new Rect(0, 0, 32, 8)), 32, 8);
        using var nearest = Render(dc => dc.DrawImage(img, new Rect(0, 0, 32, 8), ImageSampling.Nearest), 32, 8);
        for (int y = 1; y < 7; y++)
            for (int x = 1; x < 31; x++)
            {
                Assert.InRange(Pixel(linear, x, y)[0], 120, 135);
                Assert.True(Pixel(nearest, x, y)[0] is 0 or 255);
            }
    }

    [Fact]
    public void Mip_level_selection_follows_the_downscale_factor()
    {
        static int Level(float texelsPerPixel) => ImagePlan.LevelFor(Matrix3x2.CreateScale(texelsPerPixel));
        Assert.Equal(0, Level(1));
        Assert.Equal(0, Level(1.41f));
        Assert.Equal(1, Level(1.42f));
        Assert.Equal(1, Level(2.8f));
        Assert.Equal(2, Level(2.9f));
        Assert.Equal(3, Level(8));
        Assert.Equal(ImagePlan.MaxLevel, Level(1e30f));
        // 非均匀：按较大的方向
        Assert.Equal(2, ImagePlan.LevelFor(Matrix3x2.CreateScale(1, 4)));
        // 旋转不改变倍数
        Assert.Equal(2, ImagePlan.LevelFor(Matrix3x2.CreateScale(4) * Matrix3x2.CreateRotation(0.7f)));
    }

    [Fact]
    public void Bgra_bitmap_draws_like_rgba()
    {
        using var rgba = TestImages.Checker(7, 5);
        using var bgra = TestImages.Checker(7, 5, PixelFormat.Bgra8888);
        using var a = Render(dc => dc.DrawImage(rgba, new Rect(0.3f, 0.6f, 23, 17)), 24, 18);
        using var b = Render(dc => dc.DrawImage(bgra, new Rect(0.3f, 0.6f, 23, 17)), 24, 18);
        Assert.Equal(a.ToRgbaUnpremultiplied(), b.ToRgbaUnpremultiplied());
    }

    [Fact]
    public void Invalidate_refreshes_cached_mip_levels()
    {
        // 动画图片每帧复用同一张位图：按（对象，版本）缓存的 mip 级别在 Invalidate 之后必须重建
        using var img = new Bitmap(new PixelSize(64, 64));
        img.Clear(Color.Parse("#ff0000"));
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 8, 8));   // 缩小 8 倍：走 mip 缓存
        var list = dc.Finish();
        using var backend = new SoftwareRenderBackend();
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize(8, 8), 1);
        target.Render(list, new RenderOptions(Color.Transparent));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(target.Bitmap, 4, 4));

        long before = img.Version;
        for (int y = 0; y < 64; y++)
        {
            var row = img.GetRow(y);
            for (int x = 0; x < 64; x++) { row[x * 4] = 0; row[x * 4 + 2] = 255; }
        }
        img.Invalidate();
        Assert.True(img.Version > before);
        target.Render(list, new RenderOptions(Color.Transparent));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(target.Bitmap, 4, 4));

        // Clear 自动递增版本
        img.Clear(Color.Parse("#00ff00"));
        target.Render(list, new RenderOptions(Color.Transparent));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(target.Bitmap, 4, 4));
    }

    [Fact]
    public void Mip_cache_drops_old_versions_and_stays_within_budget()
    {
        var context = new RasterContext();
        using var target = new Bitmap(new PixelSize(16, 16));
        using var img = TestImages.Stripes(512, 512);
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 16, 16));
        var list = dc.Finish();
        for (int i = 0; i < 20; i++)
        {
            img.Invalidate();
            context.Render(list, target, 1, new RenderOptions(Color.White));
        }
        Assert.Equal(1, context.ImageLevels.Count);
        Assert.True(context.ImageLevels.Bytes <= RasterContext.ImageCacheBudget);
    }

    [Fact]
    public void Rendering_into_a_bitmap_target_bumps_its_version()
    {
        using var backend = new SoftwareRenderBackend();
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize(4, 4), 1);
        long v = target.Bitmap.Version;
        target.Render(DisplayList.Empty, new RenderOptions(Color.White));
        Assert.True(target.Bitmap.Version > v);
    }

    [Fact]
    public void Disposed_bitmap_is_skipped_and_reported()
    {
        var img = TestImages.Checker(4, 4);
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 8, 8));
        var list = dc.Finish();
        img.Dispose();
        var degraded = new List<string>();
        using var backend = new SoftwareRenderBackend();
        backend.Degraded += degraded.Add;
        using var target = backend.CreateOffscreenTarget(new PixelSize(8, 8), 1);
        target.Render(list, new RenderOptions(Color.White));
        Assert.Contains(degraded, d => d.Contains("disposed"));
    }

    [Fact]
    public void Hostile_geometry_is_dropped_or_bounded()
    {
        using var img = TestImages.Checker(8, 8);
        var degraded = new List<string>();
        var sw = Stopwatch.StartNew();
        using var bmp = Render(dc =>
        {
            dc.DrawImage(img, new Rect(0, 0, 1e30f, 1e30f));
            dc.DrawImage(img, new Rect(-1e30f, -1e30f, 2e30f, 2e30f), new Rect(0, 0, 50, 50));
            dc.DrawImage(img, new Rect(0, 0, 8, 8), new Rect(10, 10, 1e-30f, 1e-30f));
            dc.DrawImage(img, new Rect(0, 0, 1e-30f, 1e-30f), new Rect(0, 0, 60, 60));
            dc.DrawImage(img, new Rect(float.NaN, 0, 8, 8), new Rect(0, 0, 60, 60));
            dc.DrawImage(img, new Rect(0, 0, 8, 8), new Rect(0, float.PositiveInfinity, 60, 60));
            dc.DrawImage(img, new Rect(0, 0, 60, 60), ImageSampling.Linear, float.NaN);
            dc.DrawImage(img, new Rect(0, 0, 60, 60), (ImageSampling)77, 7);
            dc.Transform(Matrix3x2.CreateScale(0, 1));
            dc.DrawImage(img, new Rect(0, 0, 60, 60));
            dc.Transform(new Matrix3x2(1e30f, 1e30f, 1e30f, 1e30f, 0, 0));
            dc.DrawImage(img, new Rect(0, 0, 60, 60));
        }, 64, 64, degraded: degraded);
        Assert.True(sw.ElapsedMilliseconds < 20_000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Drawing_context_drops_invalid_image_commands()
    {
        using var img = TestImages.Checker(8, 8);
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 0, 5));
        dc.DrawImage(img, new Rect(0, 0, 8, 8), new Rect(0, 0, 5, -1));
        dc.DrawImage(img, new Rect(20, 20, 8, 8), new Rect(0, 0, 5, 5));      // 源矩形完全在位图外
        dc.DrawImage(img, new Rect(0, 0, 5, 5), ImageSampling.Linear, 0);
        dc.DrawImage(img, new Rect(0, 0, 5, 5), ImageSampling.Linear, -1);
        Assert.Equal(0, dc.Finish().CommandCount);
    }

    [Fact]
    public void Image_fan_out_counts_toward_the_frame_work_budget()
    {
        // 嵌套扇出：2^20 次全目标的 DrawImage，每帧工作量上限截断（而不是画上几分钟）
        using var img = TestImages.Checker(64, 64);
        var leaf = new DrawingContext().Let(dc => dc.DrawImage(img, new Rect(0, 0, 256, 256), ImageSampling.Linear, 0.5f));
        var list = leaf;
        for (int i = 0; i < 20; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        var degraded = new List<string>();
        RasterContext.WorkBudgetOverride = 1L << 24;
        try
        {
            using var backend = new SoftwareRenderBackend();
            backend.Degraded += degraded.Add;
            using var target = backend.CreateOffscreenTarget(new PixelSize(256, 256), 1);
            var sw = Stopwatch.StartNew();
            target.Render(list, new RenderOptions(Color.White));
            Assert.True(sw.ElapsedMilliseconds < 20_000, $"took {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            RasterContext.WorkBudgetOverride = null;
        }
        Assert.Contains(degraded, d => d.Contains("work limit"));
    }

    [Fact]
    public void Mip_construction_counts_toward_the_frame_work_budget()
    {
        // 大位图的许多不同源矩形各自缩小：每个都要从裁剪区构造 mip，构造代价计入工作量
        using var img = TestImages.Stripes(2048, 2048);
        var dc = new DrawingContext();
        for (int i = 0; i < 400; i++) dc.DrawImage(img, new Rect(i, i, 1500, 1500), new Rect(0, 0, 4, 4));
        var degraded = new List<string>();
        RasterContext.WorkBudgetOverride = 1L << 26;
        try
        {
            using var backend = new SoftwareRenderBackend();
            backend.Degraded += degraded.Add;
            using var target = backend.CreateOffscreenTarget(new PixelSize(8, 8), 1);
            target.Render(dc.Finish(), new RenderOptions(Color.White));
        }
        finally
        {
            RasterContext.WorkBudgetOverride = null;
        }
        Assert.Contains(degraded, d => d.Contains("work limit"));
    }

    [Fact]
    public void Image_scenes_use_a_small_fraction_of_the_work_limit()
    {
        var context = new RasterContext();
        foreach (var (name, w, h, draw) in ImageScenes.All)
        {
            var dc = new DrawingContext();
            draw(dc);
            using var bmp = new Bitmap(new PixelSize(w * 2, h * 2));
            context.Render(dc.Finish(), bmp, 2, new RenderOptions(Color.White));
            Assert.True(context.FrameWork > 0 && context.FrameWork < RasterContext.MaxWorkPerFrame / 1000, $"{name}: {context.FrameWork}");
        }
    }

    [Fact]
    public void Mip_bytes_built_in_one_frame_are_capped_and_the_rest_draw_unfiltered()
    {
        // 评审发现：缓存只在帧开始时收缩，本帧用到的条目不淘汰。同一张大位图的许多不同源矩形各自缩小一半，
        // 8×8 的目标上一帧就分配了 344 MB 的 mip（上限只有工作量 2^30 ≈ 1 GB），超出预算的条目下一帧又全部重建。
        using var img = TestImages.Stripes(512, 512);
        var dc = new DrawingContext();
        for (int i = 0; i < 40; i++) dc.DrawImage(img, new Rect(i, i, 400, 400), new Rect(0, 0, 200, 200));
        var list = dc.Finish();
        var degraded = new List<string>();
        var context = new RasterContext(degraded.Add);
        RasterContext.LevelBytesPerFrameOverride = 1L << 20;   // 每个 mip 200×200×4 = 160 000 字节，上限够 6 个
        try
        {
            using var bmp = new Bitmap(new PixelSize(8, 8));
            context.Render(list, bmp, 1, new RenderOptions(Color.Transparent));
            Assert.Equal(6, context.ImageLevels.Builds);
            Assert.True(context.ImageLevels.Bytes <= 1L << 20, $"{context.ImageLevels.Bytes} bytes of mips");
            Assert.Contains(degraded, d => d.Contains("without prefiltering"));
            // 超出上限的图片仍然画出（从第 0 级采样），而不是被跳过
            Assert.Equal(255, Pixel(bmp, 3, 3)[3]);
        }
        finally
        {
            RasterContext.LevelBytesPerFrameOverride = null;
        }
    }

    [Fact]
    public void Rotated_image_outside_the_clip_builds_no_mip()
    {
        // 评审发现：非缩放平移的变换下，DrawImage 在检查是否与裁剪区相交之前就构造 mip（屏幕外 / 损伤矩形外的旋转图片每帧付代价）
        using var img = TestImages.Stripes(512, 512);
        var dc = new DrawingContext();
        dc.Transform(Matrix3x2.CreateRotation(0.3f));
        dc.DrawImage(img, new Rect(5000, 5000, 100, 100));
        dc.DrawImage(img, new Rect(40, 40, 100, 100));     // 旋转后的包围盒 y ∈ [50, 175]
        var list = dc.Finish();
        var context = new RasterContext();
        using var bmp = new Bitmap(new PixelSize(64, 64));
        context.Render(list, bmp, 1, new RenderOptions(Color.Transparent, new PixelRect(40, 0, 24, 8)));
        Assert.Equal(0, context.ImageLevels.Builds);       // 两张都在损伤矩形之外
        context.Render(list, bmp, 1, new RenderOptions(Color.Transparent));
        Assert.Equal(1, context.ImageLevels.Builds);       // 只有屏幕内的那张
    }
}
