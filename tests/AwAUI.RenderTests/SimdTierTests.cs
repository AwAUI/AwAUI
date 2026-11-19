using System.Numerics;
using System.Runtime.InteropServices;
using AwAUI.Graphics;
using AwAUI.Render.Raster;

namespace AwAUI.RenderTests;

/// <summary>强制档位是进程级开关：这些测试不与其他测试并行，避免互相干扰。</summary>
[CollectionDefinition(nameof(SimdTierCollection), DisableParallelization = true)]
public class SimdTierCollection { }

/// <summary>
/// 光栅器三档内核（标量 / Vector&lt;T&gt; / Vector128，design/11「性能分级」）必须逐字节一致：
/// 同一输入在每一档下渲染，与标量档逐字节比较。
/// </summary>
[Collection(nameof(SimdTierCollection))]
public class SimdTierTests
{
    static readonly SimdTier[] VectorTiers = { SimdTier.Vector, SimdTier.Vector128 };

    static T WithTier<T>(SimdTier tier, Func<T> action)
    {
        var old = Kernels.ForcedTier;
        Kernels.ForcedTier = tier;
        try { return action(); }
        finally { Kernels.ForcedTier = old; }
    }

    static byte[] Render(DisplayList list, int width, int height, double scale, Color clear, PixelFormat format)
    {
        int w = (int)Math.Ceiling(width * scale), h = (int)Math.Ceiling(height * scale);
        using var bitmap = new Bitmap(new PixelSize(w, h), format);
        new RasterContext().Render(list, bitmap, scale, new RenderOptions(clear));
        var bytes = new byte[w * h * 4];
        for (int y = 0; y < h; y++) bitmap.GetRow(y).Slice(0, w * 4).CopyTo(bytes.AsSpan(y * w * 4));
        return bytes;
    }

    static void AssertIdentical(string what, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int rowBytes = 0)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i]) continue;
            string where = rowBytes > 0 ? $"pixel ({i % rowBytes / 4}, {i / rowBytes}) channel {i % 4}" : $"byte {i}";
            Assert.Fail($"{what}: {where} differs: scalar {expected[i]}, vector {actual[i]}.");
        }
    }

    static void AssertTiersIdentical(string name, Action<DrawingContext> draw, int width, int height)
    {
        var dc = new DrawingContext();
        draw(dc);
        var list = dc.Finish();
        foreach (var scale in new[] { 1.0, 2.0, 1.37 })
            foreach (var format in new[] { PixelFormat.Rgba8888, PixelFormat.Bgra8888 })
                foreach (var clear in new[] { Color.White, Color.Transparent, new Color(0.2f, 0.4f, 0.6f, 0.5f) })
                {
                    var expected = WithTier(SimdTier.Scalar, () => Render(list, width, height, scale, clear, format));
                    foreach (var tier in VectorTiers)
                    {
                        var actual = WithTier(tier, () => Render(list, width, height, scale, clear, format));
                        AssertIdentical($"{name} @{scale}x {format} clear={clear} tier={tier}", expected, actual,
                            (int)Math.Ceiling(width * scale) * 4);
                    }
                }
    }

    public static IEnumerable<object[]> SceneNames => Scenes.All.Concat(TextScenes.All).Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Golden_scene_is_identical_across_tiers(string name)
    {
        var scene = Scenes.All.Concat(TextScenes.All).Single(s => s.Name == name);
        AssertTiersIdentical(name, scene.Draw, scene.Width, scene.Height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Random_scene_is_identical_across_tiers(int seed)
    {
        AssertTiersIdentical($"random#{seed}", dc => RandomScene(new Random(seed), dc, 97, 83), 97, 83);
    }

    [Fact]
    public void Forcing_a_tier_selects_it()
    {
        Assert.Equal(SimdTier.Scalar, WithTier(SimdTier.Scalar, () => Kernels.Active));
        Assert.Equal(SimdTier.Vector, WithTier(SimdTier.Vector, () => Kernels.Active));
#if NET8_0_OR_GREATER
        Assert.Equal(SimdTier.Vector128, WithTier(SimdTier.Vector128, () => Kernels.Active));
#endif
        Kernels.ForcedTier = null;
        Assert.Equal(Kernels.Best, Kernels.Active);
    }

    // ---------------------------------------------------------------- 内核级随机输入

    static readonly int[] Lengths = { 0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 33, 64, 257, 1283 };

    static float RandomCoverage(Random r)
    {
        switch (r.Next(12))
        {
            case 0: return 0f;
            case 1: return -0f;
            case 2: return 1f;
            case 3: return 1f + 1e-6f;            // 模糊蒙版的漂移可能略大于 1
            case 4: return -1e-7f;                // 或略小于 0
            case 5: return (float)r.NextDouble() * 1e-3f;
            case 6: return 0.5f;
            default: return (float)r.NextDouble();
        }
    }

    static Vector4 RandomPremultiplied(Random r)
    {
        switch (r.Next(8))
        {
            case 0: return new Vector4((float)r.NextDouble(), (float)r.NextDouble(), (float)r.NextDouble(), 1f);
            case 1: return Vector4.Zero;
            case 2: return new Vector4(1.2f, -0.1f, 0.5f, 1.05f); // 超范围值也要一致
            default:
            {
                float a = (float)r.NextDouble();
                return new Vector4((float)r.NextDouble() * a, (float)r.NextDouble() * a, (float)r.NextDouble() * a, a);
            }
        }
    }

    /// <summary>全覆盖段、零覆盖段与随机覆盖交替，覆盖向量内核的整组快速路径与尾部。</summary>
    static float[] RandomCoverageRow(Random r, int n)
    {
        var cov = new float[n];
        int i = 0;
        while (i < n)
        {
            int run = r.Next(1, 13);
            int kind = r.Next(4);
            for (int j = 0; j < run && i < n; j++, i++)
                cov[i] = kind == 0 ? 1f : kind == 1 ? 0f : RandomCoverage(r);
        }
        return cov;
    }

    [Fact]
    public void SrcOver_kernels_are_identical_across_tiers()
    {
        var r = new Random(1234);
        for (int iter = 0; iter < 40; iter++)
            foreach (int n in Lengths)
                foreach (bool bgra in new[] { false, true })
                {
                    var src = new Vector4[n];
                    bool constant = iter % 4 == 0;
                    var color = RandomPremultiplied(r);
                    for (int i = 0; i < n; i++) src[i] = constant ? color : RandomPremultiplied(r);
                    var cov = RandomCoverageRow(r, n);
                    var dst = new byte[n * 4];
                    r.NextBytes(dst);

                    var expected = (byte[])dst.Clone();
                    WithTier(SimdTier.Scalar, () => { Blender.BlendRow(expected, bgra, src, cov, BlendMode.SrcOver); return 0; });
                    foreach (var tier in VectorTiers)
                    {
                        var actual = (byte[])dst.Clone();
                        WithTier(tier, () => { Blender.BlendRow(actual, bgra, src, cov, BlendMode.SrcOver); return 0; });
                        AssertIdentical($"BlendRow n={n} bgra={bgra} tier={tier}", expected, actual);
                    }

                    // 纯色：与「先填充着色结果再按逐像素颜色混合」的标量参考也必须一致
                    var solidExpected = (byte[])dst.Clone();
                    var filled = Enumerable.Repeat(color, n).ToArray();
                    WithTier(SimdTier.Scalar, () => { Blender.BlendRow(solidExpected, bgra, filled, cov, BlendMode.SrcOver); return 0; });
                    foreach (var tier in new[] { SimdTier.Scalar, SimdTier.Vector, SimdTier.Vector128 })
                    {
                        var actual = (byte[])dst.Clone();
                        WithTier(tier, () => { Kernels.SrcOverSolid(actual, bgra, color, cov); return 0; });
                        AssertIdentical($"SrcOverSolid n={n} bgra={bgra} tier={tier} color={color}", solidExpected, actual);
                    }
                }
    }

    static byte[] Bits(float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    [Fact]
    public void Coverage_accumulation_is_identical_across_tiers()
    {
        var r = new Random(99);
        for (int iter = 0; iter < 60; iter++)
            foreach (int n in Lengths)
            {
                // 稀疏的边缘单元 + 大段零（形状内部），偶尔出现大累积值（奇偶规则的大数退路）
                var cells = new float[n + 2];
                for (int i = 0; i < cells.Length; i++)
                {
                    int k = r.Next(10);
                    cells[i] = k < 6 ? 0f : k < 9 ? (float)(r.NextDouble() * 2 - 1) : (iter % 7 == 0 ? 3e7f : (float)r.Next(-3, 4));
                }
                float acc = iter % 3 == 0 ? 0f : (float)(r.NextDouble() * 4 - 2);
                foreach (bool nonZero in new[] { true, false })
                    foreach (bool aa in new[] { true, false })
                    {
                        var expected = new float[n];
                        bool anyExpected = WithTier(SimdTier.Scalar, () => Kernels.AccumulateCoverage(cells, acc, expected, nonZero, aa));
                        foreach (var tier in VectorTiers)
                        {
                            var actual = new float[n];
                            bool any = WithTier(tier, () => Kernels.AccumulateCoverage(cells, acc, actual, nonZero, aa));
                            AssertIdentical($"AccumulateCoverage n={n} nonZero={nonZero} aa={aa} tier={tier}", Bits(expected), Bits(actual));
                            Assert.Equal(anyExpected, any);
                        }
                    }
            }
    }

    [Fact]
    public void Elementwise_kernels_are_identical_across_tiers()
    {
        var r = new Random(7);
        float[] Random(int n) => Enumerable.Range(0, n).Select(_ => RandomCoverage(r) * (r.Next(5) == 0 ? 3f : 1f)).ToArray();
        for (int iter = 0; iter < 20; iter++)
            foreach (int n in Lengths)
            {
                var a = Random(n);
                var b = Random(n);
                float s = RandomCoverage(r);
                float t0 = (float)(r.NextDouble() * 4 - 2), dt = (float)(r.NextDouble() * 0.02 - 0.01);
                float dx0 = (float)(r.NextDouble() * 200 - 100), dy0 = (float)(r.NextDouble() * 200 - 100);
                float sx = (float)(r.NextDouble() * 2 - 1), sy = (float)(r.NextDouble() * 2 - 1), invR = (float)(r.NextDouble() * 0.05);

                float[] Run(SimdTier tier, Action<float[]> op) => WithTier(tier, () => { var v = (float[])a.Clone(); op(v); return v; });
                var ops = new (string Name, Action<float[]> Op)[]
                {
                    ("Scale", v => Kernels.Scale(b, s, v)),
                    ("Multiply", v => Kernels.Multiply(v, b)),
                    ("MultiplyByComplement", v => Kernels.MultiplyByComplement(v, b)),
                    ("Lerp", v => Kernels.Lerp(a, b, s, v)),
                    ("Ramp", v => Kernels.Ramp(v, t0, dt, false)),
                    ("Ramp+clamp", v => Kernels.Ramp(v, t0, dt, true)),
                    ("RadialDistance", v => Kernels.RadialDistance(v, dx0, dy0, sx, sy, invR, false)),
                    ("RadialDistance+clamp", v => Kernels.RadialDistance(v, dx0, dy0, sx, sy, invR, true)),
                };
                foreach (var (name, op) in ops)
                {
                    var expected = Run(SimdTier.Scalar, op);
                    foreach (var tier in VectorTiers)
                        AssertIdentical($"{name} n={n} tier={tier}", Bits(expected), Bits(Run(tier, op)));
                }

                var fill = new byte[n * 4];
                var expectedFill = WithTier(SimdTier.Scalar, () => { var f = new byte[n * 4]; Kernels.FillPixels(f, 1, 2, 3, 4); return f; });
                foreach (var tier in VectorTiers)
                    AssertIdentical($"FillPixels n={n} tier={tier}", expectedFill,
                        WithTier(tier, () => { var f = new byte[n * 4]; Kernels.FillPixels(f, 1, 2, 3, 4); return f; }));
            }
    }

    // ---------------------------------------------------------------- 随机场景

    static readonly BlendMode[] Modes =
    {
        BlendMode.SrcOver, BlendMode.SrcOver, BlendMode.SrcOver, BlendMode.SrcOver, BlendMode.Multiply, BlendMode.Screen,
        BlendMode.Plus, BlendMode.DstIn, BlendMode.DstOut, BlendMode.Src, BlendMode.Difference, BlendMode.SoftLight,
    };

    static void RandomScene(Random r, DrawingContext dc, int width, int height)
    {
        float F(float min, float max) => min + (float)r.NextDouble() * (max - min);
        Point P() => new(F(-10, width + 10), F(-10, height + 10));
        Rect R() { var p = P(); return new Rect(p.X, p.Y, F(0.3f, width * 0.8f), F(0.3f, height * 0.8f)); }
        Color C() => r.Next(3) == 0 ? new Color(F(0, 1), F(0, 1), F(0, 1)) : new Color(F(0, 1), F(0, 1), F(0, 1), F(0, 1));
        GradientStop[] Stops()
        {
            int n = r.Next(2, 5);
            return Enumerable.Range(0, n).Select(i => new GradientStop(i == 0 ? 0 : i == n - 1 ? 1 : F(0, 1), C())).OrderBy(s => s.Offset).ToArray();
        }
        Paint Paint()
        {
            var mode = Modes[r.Next(Modes.Length)];
            bool aa = r.Next(6) != 0;
            var spread = (SpreadMode)r.Next(3);
            Matrix3x2? transform = r.Next(3) == 0 ? Matrix3x2.CreateRotation(F(0, 3)) * Matrix3x2.CreateScale(F(0.5f, 2f)) : null;
            switch (r.Next(6))
            {
                case 0: return new Paint(new LinearGradientBrush(P(), P(), Stops(), spread, transform), mode, aa);
                case 1: return new Paint(new RadialGradientBrush(P(), F(1, 60), Stops(), spread, transform), mode, aa);
                case 2: return new Paint(new ConicGradientBrush(P(), F(0, 6), Stops(), transform), mode, aa);
                default: return new Paint(C(), mode, aa);
            }
        }
        VectorPath Polygon()
        {
            var pb = new PathBuilder { FillRule = r.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd };
            int n = r.Next(3, 10);
            pb.MoveTo(P());
            for (int i = 1; i < n; i++)
            {
                if (r.Next(3) == 0) pb.QuadTo(P(), P());
                else pb.LineTo(P());
            }
            return pb.Close().ToPath();
        }

        // 'S' = Save，'L' = PushLayer：两者共用一个栈，必须按嵌套顺序关闭
        var open = new Stack<char>();
        int ops = r.Next(12, 30);
        for (int i = 0; i < ops; i++)
        {
            switch (r.Next(14))
            {
                case 0: dc.FillRect(R(), Paint()); break;
                case 1: dc.FillRoundRect(new RoundRect(R(), F(0, 20)), Paint()); break;
                case 2: dc.FillCircle(P(), F(1, 40), Paint()); break;
                case 3: dc.FillPath(Polygon(), Paint()); break;
                case 4: dc.StrokePath(Polygon(), Paint(), new Stroke(F(0.3f, 8), (StrokeCap)r.Next(3), (StrokeJoin)r.Next(3))); break;
                case 5:
                    dc.DrawBoxShadow(new RoundRect(R(), F(0, 16)),
                        new BoxShadow(F(-8, 8), F(-8, 8), r.Next(4) == 0 ? 0 : F(0.5f, 24), F(-4, 6), C(), r.Next(3) == 0));
                    break;
                case 6:
                    dc.Save(); open.Push('S');
                    dc.ClipRect(R(), r.Next(4) != 0);
                    break;
                case 7:
                    dc.Save(); open.Push('S');
                    if (r.Next(2) == 0) dc.ClipRoundRect(new RoundRect(R(), F(0, 20)));
                    else dc.ClipPath(Polygon(), r.Next(4) != 0);
                    break;
                case 8:
                    dc.Save(); open.Push('S');
                    dc.Transform(Matrix3x2.CreateRotation(F(-1, 1), new Vector2(width / 2f, height / 2f)));
                    break;
                case 9:
                    dc.Save(); open.Push('S');
                    dc.Translate(F(-5, 5), F(-5, 5));
                    break;
                case 10:
                    if (open.Count > 0 && open.Peek() == 'S') { dc.Restore(); open.Pop(); }
                    break;
                case 11:
                    if (open.Count(c => c == 'L') < 3)
                    {
                        dc.PushLayer(new LayerOptions(F(0, 1), Modes[r.Next(Modes.Length)], r.Next(2) == 0 ? R() : null));
                        open.Push('L');
                    }
                    break;
                case 12:
                    if (open.Count > 0 && open.Peek() == 'L') { dc.PopLayer(); open.Pop(); }
                    break;
                default:
                    dc.FillRect(new Rect(r.Next(-4, width), r.Next(-4, height), r.Next(1, width), r.Next(1, height)), Paint());
                    break;
            }
        }
        while (open.Count > 0)
        {
            if (open.Pop() == 'S') dc.Restore();
            else dc.PopLayer();
        }
    }
}
