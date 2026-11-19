using System.Diagnostics;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;
using AwAUI.Render.Raster;
using Xunit.Abstractions;

namespace AwAUI.RenderTests;

/// <summary>字形掩码缓存与字形绘制的行为测试。</summary>
public class GlyphTests
{
    readonly ITestOutputHelper _output;

    public GlyphTests(ITestOutputHelper output) => _output = output;

    static Bitmap RenderWith(RasterContext context, DisplayList list, int width, int height, double scale = 1)
    {
        var bitmap = new Bitmap(new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)));
        context.Render(list, bitmap, scale, new RenderOptions(Color.White));
        return bitmap;
    }

    static DisplayList Record(Action<DrawingContext> draw)
    {
        var dc = new DrawingContext();
        draw(dc);
        return dc.Finish();
    }

    static GlyphKey Key(int glyph, int size = 16 * GlyphKey.SizeScale) =>
        new(TextScenes.Noto, default, (ushort)glyph, size, 0, GlyphStyle.None);

    static GlyphMask Mask(int glyph, int bytes) => new(Key(glyph), new byte[bytes], 0, 0, bytes, 1);

    // ---------------------------------------------------------------- 缓存本身

    [Fact]
    public void Cache_evicts_least_recently_used_entries_beyond_the_entry_limit()
    {
        var cache = new GlyphCache(maxEntries: 2, maxBytes: 1 << 20);
        cache.Add(Mask(1, 10));
        cache.Add(Mask(2, 10));
        Assert.True(cache.TryGet(Key(1), out _));      // 1 变为最近使用
        cache.Add(Mask(3, 10));                         // 淘汰 2
        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache.Evictions);
        Assert.False(cache.TryGet(Key(2), out _));
        Assert.True(cache.TryGet(Key(1), out _));
        Assert.True(cache.TryGet(Key(3), out _));
        Assert.Equal(3, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void Cache_respects_the_byte_budget()
    {
        long entry = 100 + GlyphMask.Overhead;
        var cache = new GlyphCache(maxEntries: 1000, maxBytes: entry * 3);
        for (int i = 0; i < 10; i++) cache.Add(Mask(i, 100));
        Assert.Equal(3, cache.Count);
        Assert.Equal(entry * 3, cache.Bytes);
        Assert.Equal(7, cache.Evictions);
        // 单个条目超过预算时仍保留它本身（刚加入的条目不被淘汰），其余全部淘汰
        cache.Add(Mask(100, 10_000));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(Key(100), out _));
        // 替换同键条目不重复计数
        cache.Add(Mask(100, 10));
        Assert.Equal(1, cache.Count);
        Assert.Equal(10 + GlyphMask.Overhead, cache.Bytes);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void Keys_distinguish_face_size_subpixel_style_and_variation()
    {
        var k = Key(5);
        Assert.Equal(k, Key(5));
        Assert.NotEqual(k, Key(6));
        Assert.NotEqual(k, Key(5, 17 * GlyphKey.SizeScale));
        Assert.NotEqual(k, new GlyphKey(TextScenes.Noto, default, 5, k.SizeBucket, 1, GlyphStyle.None));
        Assert.NotEqual(k, new GlyphKey(TextScenes.Noto, default, 5, k.SizeBucket, 0, GlyphStyle.SyntheticBold));
        // 合成加粗的加宽量（设备像素）参与键：同一设备字号在不同缩放下加宽量不同；不加粗时忽略该值
        Assert.NotEqual(new GlyphKey(TextScenes.Noto, default, 5, k.SizeBucket, 0, GlyphStyle.SyntheticBold, 100),
            new GlyphKey(TextScenes.Noto, default, 5, k.SizeBucket, 0, GlyphStyle.SyntheticBold, 120));
        Assert.Equal(k, new GlyphKey(TextScenes.Noto, default, 5, k.SizeBucket, 0, GlyphStyle.None, 100));
        var wght = new FontVariation(new VariationSetting(0x77676874, 700));
        Assert.NotEqual(k, new GlyphKey(TextScenes.Noto, wght, 5, k.SizeBucket, 0, GlyphStyle.None));
        Assert.Equal(new GlyphKey(TextScenes.Noto, wght, 5, k.SizeBucket, 0, GlyphStyle.None),
            new GlyphKey(TextScenes.Noto, new FontVariation(new VariationSetting(0x77676874, 700)), 5, k.SizeBucket, 0, GlyphStyle.None));
        var other = Text.OpenType.OpenTypeFace.Load(System.IO.Path.Combine(Golden.RepoRoot, "tests", "TestFiles", "Fonts", "NotoSans-Regular.ttf"));
        Assert.NotEqual(k, new GlyphKey(other, default, 5, k.SizeBucket, 0, GlyphStyle.None));
    }

    // ---------------------------------------------------------------- 渲染中的缓存

    [Fact]
    public void Second_frame_is_served_from_the_cache()
    {
        var run = TextScenes.Run("hello hello", 16);
        int unique = run.Glyphs.ToArray().Distinct().Count();
        var list = Record(dc => dc.DrawGlyphRun(run, new Point(10, 30), Color.Black));
        var context = new RasterContext();
        using (RenderWith(context, list, 200, 50)) { }
        var cache = context.GlyphCache;
        Assert.Equal(unique, cache.Count);
        Assert.Equal(unique, cache.Misses);
        Assert.Equal(run.Count - unique, cache.Hits);

        using (RenderWith(context, list, 200, 50)) { }
        Assert.Equal(unique, cache.Misses);
        Assert.Equal(2 * run.Count - unique, cache.Hits);
    }

    [Fact]
    public void Subpixel_offsets_are_quantized_to_quarter_pixels_and_y_is_snapped()
    {
        var run = TextScenes.Run("l", 16);
        var context = new RasterContext();
        // 0.1 与 0.0 量化到同一档；0.3 → 1/4；0.9 → 下一个整像素的 0 档；y 的小数部分不影响键
        foreach (var x in new[] { 10f, 10.1f, 10.3f, 10.9f })
            foreach (var y in new[] { 30f, 30.4f })
                using (RenderWith(context, Record(dc => dc.DrawGlyphRun(run, new Point(x, y), Color.Black)), 40, 40)) { }
        Assert.Equal(2, context.GlyphCache.Count);
    }

    [Fact]
    public void Rotated_and_huge_text_bypasses_the_cache()
    {
        var context = new RasterContext();
        var list = Record(dc =>
        {
            dc.Save();
            dc.Transform(Matrix3x2.CreateRotation(0.3f));
            dc.DrawGlyphRun(TextScenes.Run("Rotated", 16), new Point(10, 20), Color.Black);
            dc.Restore();
            dc.Save();
            dc.Transform(Matrix3x2.CreateScale(2, 1));
            dc.DrawGlyphRun(TextScenes.Run("Wide", 16), new Point(10, 40), Color.Black);
            dc.Restore();
            dc.DrawGlyphRun(TextScenes.Run("Huge", RasterContext.MaxCachedGlyphSize + 1), new Point(0, 120), Color.Black);
        });
        using var bmp = RenderWith(context, list, 200, 150);
        Assert.Equal(0, context.GlyphCache.Count);
        int inked = 0;   // 大字形确实画出来了
        for (int y = 60; y < 120; y++)
            for (int x = 0; x < 200; x++)
                if (bmp.GetPixel(x, y) != Color.White) inked++;
        Assert.True(inked > 2000, $"only {inked} pixels inked");
    }

    [Fact]
    public void Uniform_scale_is_folded_into_the_device_size()
    {
        var run = TextScenes.Run("o", 10);
        var context = new RasterContext();
        using (RenderWith(context, Record(dc => dc.DrawGlyphRun(run, new Point(4, 12), Color.Black)), 20, 20, scale: 2)) { }
        Assert.True(context.GlyphCache.TryGet(new GlyphKey(TextScenes.Noto, default, run.Glyphs[0], 20 * GlyphKey.SizeScale, 0, GlyphStyle.None), out var mask));
        Assert.InRange(mask.Height, 10, 13);   // 'o' 的 x 高度约 0.54 em：20px 时约 11 px
    }

    [Fact]
    public void Glyph_masks_match_filling_the_outlines_directly()
    {
        // 字形之间留足空隙（避免同一像素被两个字形的边缘覆盖），位置的小数部分恰为 1/4 的倍数、y 为整数：
        // 掩码路径与直接填充路径应只差 8 位量化误差
        var face = TextScenes.Noto;
        var text = "agQ@Ж&é";
        var glyphs = new ushort[text.Length];
        var positions = new Point[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            face.TryGetGlyph(text[i], out glyphs[i]);
            positions[i] = new Point(i * 33 + i * 0.25f, (i % 3) * 7);
        }
        foreach (var size in new[] { 9f, 15f, 24f })
        {
            var run = new GlyphRun(face, size, glyphs, positions);
            var origin = new Point(6, 30);
            var viaMask = Record(dc => dc.DrawGlyphRun(run, origin, Color.Parse("#202060")));
            var viaPath = Record(dc =>
            {
                var pb = new PathBuilder();
                float s = size / face.Metrics.UnitsPerEm;
                for (int i = 0; i < glyphs.Length; i++)
                    face.GetOutline(glyphs[i], default, pb, Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(origin.X + positions[i].X, origin.Y + positions[i].Y));
                dc.FillPath(pb.ToPath(), Color.Parse("#202060"));
            });
            using var a = RenderWith(new RasterContext(), viaMask, 260, 60);
            using var b = RenderWith(new RasterContext(), viaPath, 260, 60);
            int maxDiff = 0, inked = 0;
            for (int y = 0; y < a.Height; y++)
            {
                var ra = a.GetRow(y);
                var rb = b.GetRow(y);
                for (int i = 0; i < ra.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(ra[i] - rb[i]));
                for (int i = 0; i < rb.Length; i += 4) if (rb[i] != 255) inked++;
            }
            Assert.True(inked > 50, "nothing drawn");
            Assert.True(maxDiff <= 2, $"size {size}: max channel difference {maxDiff}");
        }
    }

    [Fact]
    public void Aliased_text_has_hard_edges_and_its_own_cache_entries()
    {
        var run = TextScenes.Run("Wave", 15);
        var context = new RasterContext();
        var list = Record(dc =>
        {
            dc.DrawGlyphRun(run, new Point(4.3f, 20), new Paint(Color.Black, antiAlias: false));
            dc.DrawGlyphRun(run, new Point(4.3f, 44), Color.Black);
        });
        using var bmp = RenderWith(context, list, 60, 50);
        int black = 0, grey = 0;
        for (int y = 0; y < 26; y++)
            for (int x = 0; x < 60; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c == Color.Black) black++;
                else if (c != Color.White) grey++;
            }
        Assert.True(black > 40, $"only {black} black pixels");
        Assert.Equal(0, grey);
        // 抗锯齿与不抗锯齿的掩码分别缓存（同一字形、字号、子像素档）
        Assert.Equal(2 * run.Glyphs.ToArray().Distinct().Count(), context.GlyphCache.Count);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(1.5)]
    public void Synthetic_bold_extent_follows_the_logical_size_at_any_device_scale(double scale)
    {
        // 合成加粗的加宽量由逻辑字号决定（与 Skia 一致），与设备缩放无关：缓存路径在任意均匀缩放下
        // 都应等于「在局部坐标中按 GetSyntheticBoldExtent(逻辑字号) 加宽后再整体缩放」，也就与回退路径一致。
        // 回归：此前缓存路径按设备字号计算加宽量，2x 下的加粗比 1x 与旋转回退路径都细
        var face = TextScenes.Noto;
        const float size = 12;
        var text = "lHoW";
        var glyphs = new ushort[text.Length];
        var positions = new Point[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            face.TryGetGlyph(text[i], out glyphs[i]);
            positions[i] = new Point(i * 20, 0);
        }
        var run = new GlyphRun(face, size, glyphs, positions, GlyphRunFlags.SyntheticBold);
        var origin = new Point(4, 20);
        var color = Color.Parse("#202060");
        var viaMask = Record(dc => dc.DrawGlyphRun(run, origin, color));
        var viaPath = Record(dc =>
        {
            var pb = new PathBuilder();
            float s = size / face.Metrics.UnitsPerEm;
            float w = GlyphRun.GetSyntheticBoldExtent(size);
            var offsets = new[] { new Vector2(0, 0), new Vector2(w, 0), new Vector2(w / 2, -w / 2), new Vector2(w / 2, w / 2) };
            for (int i = 0; i < glyphs.Length; i++)
                foreach (var o in offsets)
                    face.GetOutline(glyphs[i], default, pb, Matrix3x2.CreateScale(s)
                        * Matrix3x2.CreateTranslation(origin.X + positions[i].X + o.X, origin.Y + positions[i].Y + o.Y));
            dc.FillPath(pb.ToPath(), color);
        });
        using var a = RenderWith(new RasterContext(), viaMask, 90, 30, scale);
        using var b = RenderWith(new RasterContext(), viaPath, 90, 30, scale);
        int maxDiff = 0;
        for (int y = 0; y < a.Height; y++)
        {
            var ra = a.GetRow(y);
            var rb = b.GetRow(y);
            for (int i = 0; i < ra.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(ra[i] - rb[i]));
        }
        Assert.True(maxDiff <= 2, $"@{scale}x: max channel difference {maxDiff}");
    }

    [Fact]
    public void Hostile_runs_do_not_crash_or_allocate_unbounded_masks()
    {
        var face = TextScenes.Noto;
        var context = new RasterContext();
        var glyphs = new ushort[] { 1, 40, ushort.MaxValue, 50 };
        var list = Record(dc =>
        {
            // 无效字形 ID、极大坐标与极端字号
            dc.DrawGlyphRun(new GlyphRun(face, 16, glyphs, new[] { new Point(0, 0), new Point(1e30f, 0), new Point(10, 0), new Point(-1e9f, 5e8f) }),
                new Point(5, 20), Color.Black);
            dc.DrawGlyphRun(new GlyphRun(face, 1e30f, glyphs, new Point[4]), new Point(5, 20), Color.Black);
            dc.DrawGlyphRun(new GlyphRun(face, 1e-6f, glyphs, new Point[4]), new Point(5, 20), Color.Black);
            dc.Save();
            dc.Transform(Matrix3x2.CreateScale(1e6f));
            dc.DrawGlyphRun(new GlyphRun(face, 16, glyphs, new Point[4]), new Point(0, 0), Color.Black);
            dc.Restore();
        });
        var sw = Stopwatch.StartNew();
        using (RenderWith(context, list, 50, 50)) { }
        Assert.True(sw.ElapsedMilliseconds < 5000);
        Assert.True(context.GlyphCache.Bytes < 1 << 20);
        // NaN 位置的字形串不录制
        var nan = Record(dc => dc.DrawGlyphRun(new GlyphRun(face, 16, glyphs, new Point[] { new(float.NaN, 0), default, default, default }), default, Color.Black));
        Assert.True(nan.IsEmpty);
    }

    [Fact]
    public void Glyph_runs_survive_display_list_nesting_and_are_clipped()
    {
        var run = TextScenes.Run("MMMMMMMM", 20);
        var child = Record(dc => dc.DrawGlyphRun(run, new Point(0, 20), Color.Black));
        var list = Record(dc =>
        {
            dc.ClipRect(new Rect(0, 0, 30, 50));
            dc.Translate(5, 5);
            dc.DrawDisplayList(child);
        });
        using var bmp = RenderWith(new RasterContext(), list, 120, 40);
        bool inkInside = false;
        for (int x = 5; x < 30; x++)
            for (int y = 5; y < 30; y++)
                inkInside |= bmp.GetPixel(x, y) != Color.White;
        Assert.True(inkInside);
        for (int x = 30; x < 120; x++)
            for (int y = 0; y < 40; y++)
                Assert.Equal(Color.White, bmp.GetPixel(x, y));
    }

    // ---------------------------------------------------------------- 性能测量（只输出，不断言时间）

    [Fact]
    public void Frame_with_two_thousand_glyphs_cold_and_warm()
    {
        // 1280×880 设备像素、约 2000 个字形、14px 文本：1x（14px 设备字号）与 2x（28px 设备字号）各测一次。
        // 冷缓存 = 新的 RasterContext 第一帧；热缓存 = 同一上下文的第二帧
        var lines = new[]
        {
            "The quick brown fox jumps over the lazy dog 0123",
            "Sphinx of black quartz, judge my vow! ABCDEFGHIJ",
            "Pack my box with five dozen liquor jugs; klmnopq",
            "How vexingly quick daft zebras jump: 456789 ÅÉÑÖ",
        };
        foreach (var scale in new[] { 1.0, 2.0 })
        {
            float logicalW = (float)(1280 / scale), logicalH = (float)(880 / scale);
            float size = 14;
            int rows = (int)(logicalH / (size * 1.5f));
            int columns = scale > 1 ? 2 : 1;   // 2x 下逻辑宽度只有 640：分两栏（每行末尾少量字形超出视口，被剔除）
            int perLine = 2000 / (rows * columns) + 1;
            var runs = new List<GlyphRun>();
            int glyphCount = 0;
            for (int r = 0; r < rows * columns; r++)
            {
                var text = string.Concat(Enumerable.Repeat(lines[r % lines.Length] + " ", 4)).Substring(0, perLine);
                var run = TextScenes.Run(text, size);
                runs.Add(run);
                glyphCount += run.Count;
            }
            var list = Record(dc =>
            {
                for (int r = 0; r < runs.Count; r++)
                    dc.DrawGlyphRun(runs[r], new Point(4 + (r % columns) * logicalW / 2 + (r % 4) * 0.25f, (r / columns + 1) * size * 1.5f), Color.Parse("#202124"));
            });
            int w = (int)(logicalW * scale), h = (int)(logicalH * scale);
            using var bitmap = new Bitmap(new PixelSize(w, h));

            // 预热 JIT
            new RasterContext().Render(list, bitmap, scale, new RenderOptions(Color.White));

            var cold = new List<double>();
            var warm = new List<double>();
            for (int i = 0; i < 7; i++)
            {
                var context = new RasterContext();
                var sw = Stopwatch.StartNew();
                context.Render(list, bitmap, scale, new RenderOptions(Color.White));
                cold.Add(sw.Elapsed.TotalMilliseconds);
                int cached = context.GlyphCache.Count;
                sw.Restart();
                context.Render(list, bitmap, scale, new RenderOptions(Color.White));
                warm.Add(sw.Elapsed.TotalMilliseconds);
                Assert.Equal(cached, context.GlyphCache.Count);   // 第二帧不再光栅化（未命中的只有视口外被剔除的字形）
            }
            // 对照：不缓存（同一内容在 1e-4 弧度的旋转下走路径填充）
            var pathList = Record(dc =>
            {
                dc.Transform(Matrix3x2.CreateRotation(1e-4f));
                dc.DrawDisplayList(list);
            });
            var path = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                new RasterContext().Render(pathList, bitmap, scale, new RenderOptions(Color.White));
                path.Add(sw.Elapsed.TotalMilliseconds);
            }
            // 基线：同尺寸的空帧（只清屏）
            var empty = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                new RasterContext().Render(DisplayList.Empty, bitmap, scale, new RenderOptions(Color.White));
                empty.Add(sw.Elapsed.TotalMilliseconds);
            }
            cold.Sort(); warm.Sort(); path.Sort(); empty.Sort();
            _output.WriteLine(FormattableString.Invariant(
                $"{w}x{h} @{scale}x, {glyphCount} glyphs, {size}px ({size * scale}px device), tier {Kernels.Active}: cold {cold[cold.Count / 2]:F2} ms, warm {warm[warm.Count / 2]:F2} ms, path (no cache) {path[path.Count / 2]:F2} ms, clear only {empty[empty.Count / 2]:F2} ms"));
        }
    }
}
