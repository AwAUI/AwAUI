using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render;

namespace AwAUI.RenderTests;

/// <summary>
/// 与裁剪无关的软件光栅（第六批后续事项）：同一帧整帧光栅化与只光栅化损伤矩形（<see cref="RenderOptions.Damage"/>）时，
/// 损伤矩形内的像素必须逐字节相同。UI 的局部光栅化（UIHost.PartialRaster）依赖这一点。
/// 场景由随机的「正常」内容组成（矩形、圆角、路径、描边与虚线、各种渐变、模糊阴影含大 σ 的降采样路径与内阴影、字形、
/// 位图、各种裁剪、图层与混合模式、旋转与缩放），不含会触发每帧工作量上限的恶意输入（局部光栅化的工作量更少，上限的截断点不同）。
/// 种子数默认 40，AWAUI_CLIP_SEEDS=N 加大。
/// </summary>
public class ClipIndependenceTests
{
    public static IEnumerable<object[]> Seeds =>
        Enumerable.Range(0, int.TryParse(Environment.GetEnvironmentVariable("AWAUI_CLIP_SEEDS"), out int n) && n > 0 ? n : 40)
            .Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Partial_raster_matches_full_raster_inside_the_damage_rect(int seed) => Run(seed, effects: false);

    /// <summary>
    /// 第九批：场景中还有滤镜图层（模糊含降采样路径、投影、颜色矩阵，以及它们的链）。滤镜图层的内容不受裁剪限制地绘制，
    /// 所以局部光栅化与整帧光栅化同样逐字节相同。（背景滤镜读取裁剪区之外的已绘制像素，按设计只在损伤包含其读取范围时成立，不在此测试。）
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Partial_raster_with_filter_layers_matches_full_raster(int seed) => Run(seed + 100_000, effects: true);

    static void Run(int seed, bool effects)
    {
        var rng = new Random(seed * 7919 + 17);
        double scale = new[] { 1.0, 1.5, 2.0, 1.25 }[seed % 4];
        int w = 220, h = 160;
        var list = new SceneGenerator(rng, effects).Build(18 + rng.Next(14));
        var size = new PixelSize((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale));

        using var backend = new SoftwareRenderBackend();
        var degraded = new List<string>();
        backend.Degraded += degraded.Add;
        using var full = (BitmapRenderTarget)backend.CreateOffscreenTarget(size, scale);
        full.Render(list, new RenderOptions(Color.White));
        var expected = full.Bitmap.ToRgbaUnpremultiplied();

        using var partial = (BitmapRenderTarget)backend.CreateOffscreenTarget(size, scale);
        for (int i = 0; i < 6; i++)
        {
            // 先画上一帧的内容（损伤矩形之外保留），再只重画损伤矩形
            partial.Render(list, new RenderOptions(Color.White));
            int x0 = rng.Next(size.Width - 1), y0 = rng.Next(size.Height - 1);
            var damage = new PixelRect(x0, y0, 1 + rng.Next(Math.Min(120, size.Width - x0)), 1 + rng.Next(Math.Min(90, size.Height - y0)));
            partial.Bitmap.Clear(Color.Black);   // 损伤外的旧内容不影响损伤内的结果
            partial.Render(list, new RenderOptions(Color.White, damage));
            var actual = partial.Bitmap.ToRgbaUnpremultiplied();
            int diffs = 0, maxDiff = 0, firstX = -1, firstY = -1;
            for (int y = damage.Y; y < damage.Bottom; y++)
                for (int x = damage.X; x < damage.Right; x++)
                {
                    int o = (y * size.Width + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int d = Math.Abs(expected[o + c] - actual[o + c]);
                        if (d == 0) continue;
                        if (diffs++ == 0) { firstX = x; firstY = y; }
                        maxDiff = Math.Max(maxDiff, d);
                    }
                }
            Assert.True(diffs == 0,
                $"seed {seed}, scale {scale}, damage {damage}: {diffs} channel values differ (max {maxDiff}), first at ({firstX}, {firstY}). Degradations: {string.Join("; ", degraded)}");
        }
        Assert.DoesNotContain(degraded, m => m.Contains("work limit"));
    }

    /// <summary>
    /// 批次 6 的复现：大 blur 的盒阴影（降采样网格曾对齐到裁剪区）在任意偏移的损伤矩形下与整帧光栅逐字节相同，
    /// 包括走低分辨率路径（整个阴影超过工作区像素上限）的大阴影。
    /// </summary>
    [Theory]
    [InlineData(6f, false)]
    [InlineData(40f, false)]
    [InlineData(40f, true)]
    [InlineData(300f, false)]
    public void Blurred_shadow_is_independent_of_the_damage_rect(float blur, bool inset)
    {
        var dc = new DrawingContext();
        dc.Transform(Matrix3x2.CreateTranslation(0.3f, 0.7f));
        dc.DrawBoxShadow(new RoundRect(new Rect(40, 30, 140, 90), 14), new BoxShadow(5, 7, blur, 3, Color.Parse("#20304080"), inset));
        dc.DrawBoxShadow(new RoundRect(new Rect(-500, -400, 2000, 1500), 0), new BoxShadow(0, 0, blur, 0, Color.Parse("#00000020"), false));
        var list = dc.Finish();
        var size = new PixelSize(330, 240);
        using var backend = new SoftwareRenderBackend();
        using var full = (BitmapRenderTarget)backend.CreateOffscreenTarget(size, 1.5);
        full.Render(list, new RenderOptions(Color.White));
        var expected = full.Bitmap.ToRgbaUnpremultiplied();
        using var partial = (BitmapRenderTarget)backend.CreateOffscreenTarget(size, 1.5);
        for (int dx = 0; dx < 9; dx++)
        {
            var damage = new PixelRect(13 + dx * 29, 7 + dx * 17, 37 + dx * 5, 41 + dx * 3);
            partial.Render(list, new RenderOptions(Color.White, damage));
            var actual = partial.Bitmap.ToRgbaUnpremultiplied();
            for (int y = damage.Y; y < Math.Min(size.Height, damage.Bottom); y++)
                for (int x = damage.X; x < Math.Min(size.Width, damage.Right); x++)
                {
                    int o = (y * size.Width + x) * 4;
                    for (int c = 0; c < 4; c++)
                        Assert.True(expected[o + c] == actual[o + c], $"blur {blur}, damage {damage}: pixel ({x}, {y}) differs");
                }
        }
    }

    /// <summary>「正常」UI 内容的随机场景（与 <see cref="DisplayListFuzzer"/> 不同，不含非有限值与巨大几何）。</summary>
    internal sealed class SceneGenerator
    {
        readonly Random _rng;
        readonly bool _effects;
        static readonly Lazy<Bitmap> s_image = new(() => TestImages.Checker(23, 17));

        public SceneGenerator(Random rng, bool effects = false)
        {
            _rng = rng;
            _effects = effects;
        }

        /// <summary>随机的滤镜链（1–3 个操作）。</summary>
        Filter RandomFilter()
        {
            var ops = new List<FilterOperation>();
            int n = 1 + _rng.Next(3);
            for (int i = 0; i < n; i++)
            {
                ops.Add(_rng.Next(7) switch
                {
                    0 => FilterOperation.Blur(R(3)),
                    1 => FilterOperation.Blur(4 + R(14)),
                    2 => FilterOperation.DropShadow(R(10) - 5, R(10) - 5, R(6), Col()),
                    3 => FilterOperation.Grayscale(R(1)),
                    4 => FilterOperation.HueRotate(R(360)),
                    5 => FilterOperation.Contrast(R(2)),
                    _ => FilterOperation.Blur(R(6), R(2)),
                });
            }
            return new Filter(ops);
        }

        float R(float max) => (float)_rng.NextDouble() * max;
        Color Col() => new(R(1), R(1), R(1), 0.3f + R(0.7f));
        Rect Rc() => new(R(200) - 10, R(150) - 10, 2 + R(120), 2 + R(90));

        Brush Br()
        {
            var stops = new[] { new GradientStop(0, Col()), new GradientStop(0.3f + R(0.4f), Col()), new GradientStop(1, Col()) };
            var spread = (SpreadMode)_rng.Next(3);
            return _rng.Next(5) switch
            {
                0 => new LinearGradientBrush(new Point(R(200), R(150)), new Point(R(200), R(150)), stops, spread),
                1 => new RadialGradientBrush(new Point(R(200), R(150)), 5 + R(100), stops, spread),
                2 => new RadialGradientBrush(new Point(R(200), R(150)), 20 + R(80), new Point(R(200), R(150)), R(10), stops, spread),
                3 => new ConicGradientBrush(new Point(R(200), R(150)), R(6), stops),
                _ => new SolidColorBrush(Col()),
            };
        }

        Paint Pt() => new(_rng.Next(3) == 0 ? Br() : new SolidColorBrush(Col()),
            _rng.Next(8) == 0 ? (BlendMode)_rng.Next(17) : BlendMode.SrcOver, _rng.Next(8) != 0);

        VectorPath Path()
        {
            var pb = new PathBuilder { FillRule = _rng.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd };
            pb.MoveTo(R(220), R(160));
            int n = 2 + _rng.Next(8);
            for (int i = 0; i < n; i++)
            {
                switch (_rng.Next(3))
                {
                    case 0: pb.LineTo(R(220), R(160)); break;
                    case 1: pb.QuadTo(new Point(R(220), R(160)), new Point(R(220), R(160))); break;
                    default: pb.CubicTo(new Point(R(220), R(160)), new Point(R(220), R(160)), new Point(R(220), R(160))); break;
                }
            }
            if (_rng.Next(2) == 0) pb.Close();
            if (_rng.Next(3) == 0) pb.AddCircle(new Point(R(220), R(160)), 3 + R(40));
            return pb.ToPath();
        }

        Stroke St() => new(0.5f + R(9), (StrokeCap)_rng.Next(3), (StrokeJoin)_rng.Next(3), 4,
            _rng.Next(3) == 0 ? new[] { 1 + R(8), 1 + R(6) } : null, R(5));

        public DisplayList Build(int commands)
        {
            var dc = new DrawingContext();
            var open = new Stack<bool>();
            for (int i = 0; i < commands; i++)
            {
                switch (_rng.Next(_effects ? 21 : 19))
                {
                    case 19:
                    case 20:
                        dc.PushLayer(new LayerOptions(_rng.Next(2) == 0 ? 1 : 0.3f + R(0.7f), BlendMode.SrcOver, _rng.Next(4) == 0 ? null : Rc(), RandomFilter()));
                        open.Push(true);
                        break;
                    case 0: dc.Save(); open.Push(false); break;
                    case 1:
                        if (open.Count > 0) { if (open.Pop()) dc.PopLayer(); else dc.Restore(); }
                        break;
                    case 2:
                        dc.Transform(_rng.Next(3) switch
                        {
                            0 => Matrix3x2.CreateRotation(R(1) - 0.5f, new Vector2(R(200), R(150))),
                            1 => Matrix3x2.CreateScale(0.6f + R(0.8f), 0.6f + R(0.8f), new Vector2(R(200), R(150))),
                            _ => Matrix3x2.CreateTranslation(R(20) - 10, R(20) - 10),
                        });
                        break;
                    case 3: dc.ClipRect(Rc(), _rng.Next(2) == 0); break;
                    case 4: dc.ClipRoundRect(new RoundRect(Rc(), R(20)), true); break;
                    case 5: dc.ClipPath(Path(), _rng.Next(4) != 0); break;
                    case 6: case 7: dc.FillRect(Rc(), Pt()); break;
                    case 8: dc.FillRoundRect(new RoundRect(Rc(), R(25), R(15), R(30), R(5)), Pt()); break;
                    case 9: dc.StrokeRoundRect(new RoundRect(Rc(), R(20)), Pt(), St()); break;
                    case 10: dc.FillPath(Path(), Pt()); break;
                    case 11: dc.StrokePath(Path(), Pt(), St()); break;
                    case 12: dc.DrawLine(new Point(R(220), R(160)), new Point(R(220), R(160)), Pt(), St()); break;
                    case 13:
                    {
                        float blur = _rng.Next(4) == 0 ? 30 + R(120) : R(16);
                        dc.DrawBoxShadow(new RoundRect(Rc(), R(20)), new BoxShadow(R(12) - 6, R(12) - 6, blur, R(8) - 3, Col(), _rng.Next(4) == 0));
                        break;
                    }
                    case 14:
                        dc.PushLayer(new LayerOptions(_rng.Next(2) == 0 ? 1 : 0.2f + R(0.8f),
                            _rng.Next(3) == 0 ? (BlendMode)_rng.Next(17) : BlendMode.SrcOver, _rng.Next(2) == 0 ? Rc() : null));
                        open.Push(true);
                        break;
                    case 15:
                        dc.DrawGlyphRun(TextScenes.Run("Clip 0123 ago", 8 + R(20)), new Point(R(180), 10 + R(140)), Pt());
                        break;
                    case 16:
                    {
                        var img = s_image.Value;
                        var src = _rng.Next(2) == 0 ? new Rect(0, 0, img.Width, img.Height) : new Rect(R(10), R(8), 3 + R(12), 3 + R(8));
                        dc.DrawImage(img, src, Rc(), (ImageSampling)_rng.Next(2), 0.3f + R(0.7f));
                        break;
                    }
                    case 17: dc.FillEllipse(Rc(), Pt()); break;
                    default: dc.StrokeRect(Rc(), Pt(), St()); break;
                }
            }
            return dc.Finish();
        }
    }
}
