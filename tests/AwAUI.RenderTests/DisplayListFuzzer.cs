using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.RenderTests;

/// <summary>
/// 恶意或极端的 DisplayList 生成器（软件后端与 GPU 后端的模糊测试共用；GPU 测试项目以链接方式包含本文件）：
/// 极大 / 极小 / 非有限的几何、奇异变换、超大线宽与模糊、超密虚线、深嵌套、大量图层与混合模式、带焦点的渐变、无效字形。
/// </summary>
public sealed class DisplayListFuzzer
{
    readonly Random _rng;
    public DisplayListFuzzer(int seed) => _rng = new Random(seed);

    static readonly Bitmap[] s_images = CreateImages();

    static Bitmap[] CreateImages()
    {
        var disposed = TestImages.Checker(3, 3);
        disposed.Dispose();
        return new[] { TestImages.Checker(1, 1), TestImages.Checker(7, 5), TestImages.Stripes(300, 200), disposed };
    }

    /// <summary>默认 12 个种子；设置 AWAUI_FUZZ_SEEDS=N 做更长的模糊测试。</summary>
    public static IEnumerable<object[]> Seeds =>
        Enumerable.Range(0, int.TryParse(Environment.GetEnvironmentVariable("AWAUI_FUZZ_SEEDS"), out int n) && n > 0 ? n : 12)
            .Select(i => new object[] { i });

    static readonly float[] Special =
    {
        0, 0.5f, -0.5f, 1e-30f, -1e-30f, 1e-3f, 1e7f, -1e7f, 1e30f, -1e30f, float.MaxValue, float.MinValue, 3.4e-38f,
    };

    public float F(float range = 300)
    {
        int k = _rng.Next(10);
        if (k == 0) return Special[_rng.Next(Special.Length)];
        if (k == 1) return float.NaN;
        return (float)(_rng.NextDouble() * 2 - 1) * range;
    }

    public float Pos(float range = 100) => _rng.Next(8) == 0 ? F(range) : (float)_rng.NextDouble() * range;

    public Color Color() => _rng.Next(12) == 0 ? new Color(F(2), F(2), F(2), F(2))
        : new Color((float)_rng.NextDouble(), (float)_rng.NextDouble(), (float)_rng.NextDouble(), (float)_rng.NextDouble());

    public Brush Brush()
    {
        var stops = new GradientStop[1 + _rng.Next(4)];
        for (int i = 0; i < stops.Length; i++) stops[i] = new GradientStop(_rng.Next(6) == 0 ? F(3) : (float)_rng.NextDouble(), Color());
        var spread = (SpreadMode)_rng.Next(3);
        Matrix3x2? m = _rng.Next(4) == 0 ? new Matrix3x2(F(3), F(3), F(3), F(3), F(), F()) : null;
        return _rng.Next(4) switch
        {
            0 => new SolidColorBrush(Color()),
            1 => new LinearGradientBrush(new Point(Pos(), Pos()), new Point(Pos(), Pos()), stops, spread, m),
            2 when _rng.Next(2) == 0 => new RadialGradientBrush(new Point(Pos(), Pos()), F(80), stops, spread, m),
            2 => new RadialGradientBrush(new Point(Pos(), Pos()), F(80), new Point(Pos(), Pos()), F(40), stops, spread, m),   // 带焦点
            _ => new ConicGradientBrush(new Point(Pos(), Pos()), F(7), stops, m),
        };
    }

    public Paint Paint() => new(_rng.Next(3) == 0 ? Brush() : new SolidColorBrush(Color()),
        _rng.Next(4) == 0 ? (BlendMode)_rng.Next(21) : BlendMode.SrcOver, _rng.Next(5) != 0);

    public Stroke Stroke()
    {
        float w = _rng.Next(6) == 0 ? F(1e4f) : (float)_rng.NextDouble() * 12;
        float[]? dashes = _rng.Next(4) == 0 ? new[] { _rng.Next(3) == 0 ? 1e-4f : (float)_rng.NextDouble() * 10, (float)_rng.NextDouble() * 5 } : null;
        return new Stroke(w, (StrokeCap)_rng.Next(3), (StrokeJoin)_rng.Next(3), F(10), dashes, F(20));
    }

    public Rect Rect() => new(Pos(), Pos(), _rng.Next(10) == 0 ? F() : (float)_rng.NextDouble() * 80, _rng.Next(10) == 0 ? F() : (float)_rng.NextDouble() * 80);

    public RoundRect RoundRect() => new(Rect(), new Size(Math.Abs(F(30)), Math.Abs(F(30))), new Size(Math.Abs(F(30)), Math.Abs(F(30))),
        new Size(Math.Abs(F(30)), Math.Abs(F(30))), new Size(Math.Abs(F(30)), Math.Abs(F(30))));

    public VectorPath Path()
    {
        var pb = new PathBuilder { FillRule = _rng.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd };
        int n = 1 + _rng.Next(_rng.Next(10) == 0 ? 400 : 12);
        pb.MoveTo(Pos(), Pos());
        for (int i = 0; i < n; i++)
        {
            switch (_rng.Next(5))
            {
                case 0: pb.LineTo(Pos(), Pos()); break;
                case 1: pb.QuadTo(new Point(F(), F()), new Point(Pos(), Pos())); break;
                case 2: pb.CubicTo(new Point(F(), F()), new Point(F(), F()), new Point(Pos(), Pos())); break;
                case 3: pb.Close(); break;
                default: pb.MoveTo(Pos(), Pos()); break;
            }
        }
        return pb.ToPath();
    }

    public Matrix3x2 Matrix() => _rng.Next(5) switch
    {
        0 => Matrix3x2.CreateRotation(F(7)),
        1 => Matrix3x2.CreateScale(F(4), F(4)),
        2 => new Matrix3x2(F(3), F(3), F(3), F(3), F(), F()),
        3 => Matrix3x2.CreateScale(0, 1),
        _ => Matrix3x2.CreateTranslation(F(50), F(50)),
    };

    /// <summary>随机的滤镜链：极端的标准差、偏移与系数，偶尔很长。</summary>
    public Filter Filter()
    {
        int n = _rng.Next(8) == 0 ? 40 + _rng.Next(60) : 1 + _rng.Next(3);
        var ops = new FilterOperation[n];
        for (int i = 0; i < n; i++)
        {
            ops[i] = _rng.Next(6) switch
            {
                0 => FilterOperation.Blur(_rng.Next(5) == 0 ? F(1e6f) : (float)_rng.NextDouble() * 20),
                1 => FilterOperation.Blur(F(30), F(30)),
                2 => FilterOperation.DropShadow(F(40), F(40), _rng.Next(5) == 0 ? F(1e5f) : (float)_rng.NextDouble() * 8, Color()),
                3 => FilterOperation.HueRotate(F(1000)),
                4 => FilterOperation.ColorMatrix(Enumerable.Range(0, 20).Select(_ => F(3)).ToArray()),
                _ => FilterOperation.Saturate(F(10)),
            };
        }
        return new Filter(ops);
    }

    public DisplayList Build(int commands, int depth = 0)
    {
        var dc = new DrawingContext();
        int open = 0;
        var opened = new Stack<bool>();   // true = layer
        for (int i = 0; i < commands; i++)
        {
            switch (_rng.Next(24))
            {
                case 22:
                    // 第九批：滤镜图层（含极端 σ、偏移与超长的链）
                    dc.PushLayer(new LayerOptions(F(1.5f), (BlendMode)_rng.Next(21), _rng.Next(3) == 0 ? null : Rect(), Filter()));
                    opened.Push(true);
                    break;
                case 23:
                    dc.DrawBackdrop(RoundRect(), Filter(), F(1.5f));
                    break;
                case 0: dc.Save(); opened.Push(false); open++; break;
                case 1: if (opened.Count > 0) { if (opened.Pop()) dc.PopLayer(); else dc.Restore(); } break;
                case 2: dc.Transform(Matrix()); break;
                case 3: dc.ClipRect(Rect(), _rng.Next(3) != 0); break;
                case 4: dc.ClipRoundRect(RoundRect(), _rng.Next(3) != 0); break;
                case 5: dc.ClipPath(Path(), _rng.Next(3) != 0); break;
                case 6: dc.FillRect(Rect(), Paint()); break;
                case 7: dc.FillRoundRect(RoundRect(), Paint()); break;
                case 8: dc.StrokeRect(Rect(), Paint(), Stroke()); break;
                case 9: dc.StrokeRoundRect(RoundRect(), Paint(), Stroke()); break;
                case 10: dc.DrawLine(new Point(Pos(), Pos()), new Point(Pos(), Pos()), Paint(), Stroke()); break;
                case 11: dc.FillPath(Path(), Paint()); break;
                case 12: dc.StrokePath(Path(), Paint(), Stroke()); break;
                case 13:
                    dc.DrawBoxShadow(RoundRect(), new BoxShadow(F(20), F(20), _rng.Next(5) == 0 ? F(1e6f) : (float)_rng.NextDouble() * 30, F(20), Color(), _rng.Next(2) == 0));
                    break;
                case 14:
                    dc.PushLayer(new LayerOptions(F(1.5f), (BlendMode)_rng.Next(21), _rng.Next(2) == 0 ? Rect() : null));
                    opened.Push(true);
                    break;
                case 15:
                    if (depth < 3) dc.DrawDisplayList(Build(5 + _rng.Next(10), depth + 1));
                    break;
                case 16:
                    dc.Translate(F(40), F(40));
                    break;
                case 17:
                case 18:
                {
                    // 位图：极端的源 / 目标矩形、采样方式与不透明度（包括超出范围的枚举值）、已释放的位图
                    var img = s_images[_rng.Next(s_images.Length)];
                    var src = _rng.Next(3) == 0 ? new Rect(0, 0, img.Width, img.Height) : Rect();
                    dc.DrawImage(img, src, Rect(), (ImageSampling)_rng.Next(3), _rng.Next(4) == 0 ? F(2) : 1);
                    break;
                }
                default:
                {
                    int n = 1 + _rng.Next(20);
                    var glyphs = new ushort[n];
                    var positions = new Point[n];
                    for (int k = 0; k < n; k++)
                    {
                        glyphs[k] = (ushort)_rng.Next(_rng.Next(5) == 0 ? 65536 : 300);
                        positions[k] = new Point(k * 8 + (_rng.Next(20) == 0 ? F() : 0), _rng.Next(20) == 0 ? F() : 0);
                    }
                    float size = _rng.Next(6) == 0 ? Math.Abs(F(1000)) : 4 + (float)_rng.NextDouble() * 40;
                    if (!(size >= 0) || float.IsInfinity(size)) size = 12;
                    var run = new Graphics.Text.GlyphRun(TextScenes.Noto, size, glyphs, positions, (Graphics.Text.GlyphRunFlags)_rng.Next(4));
                    dc.DrawGlyphRun(run, new Point(Pos(), Pos()), Paint());
                    break;
                }
            }
        }
        return dc.Finish();
    }
}
