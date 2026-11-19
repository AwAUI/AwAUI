using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;
using AwAUI.Text;
using AwAUI.Text.OpenType;

namespace AwAUI.RenderTests;

/// <summary>
/// 字形绘制（DrawOp.DrawGlyphRun）的参考场景：用仓库内的 Noto Sans 经真实的 OpenTypeFace + BasicTextShaper 整形。
/// 测试不受 Render → Text 的依赖规则约束。
/// </summary>
public static class TextScenes
{
    static Color C(string hex) => Color.Parse(hex);
    static GradientStop[] Stops(params (float, string)[] s) => s.Select(x => new GradientStop(x.Item1, C(x.Item2))).ToArray();

    static readonly Lazy<OpenTypeFace> s_noto = new(() =>
        OpenTypeFace.Load(System.IO.Path.Combine(Golden.RepoRoot, "tests", "TestFiles", "Fonts", "NotoSans-Regular.ttf")));

    /// <summary>同一个字型实例（字形缓存按对象引用识别字型）。</summary>
    public static OpenTypeFace Noto => s_noto.Value;

    public static GlyphRun Run(string text, float size, GlyphRunFlags flags = GlyphRunFlags.None) =>
        BasicTextShaper.Instance.Shape(new ShapeRequest(text, Noto, size)).ToGlyphRun(flags);

    public static readonly (string Name, int Width, int Height, Action<DrawingContext> Draw)[] All =
    {
        ("glyphs-sizes", 560, 400, Sizes),
        ("glyphs-subpixel", 300, 150, Subpixel),
        ("glyphs-paint", 360, 200, PaintScene),
        ("glyphs-transforms", 360, 260, Transforms),
        ("glyphs-clip-layers", 360, 200, ClipAndLayers),
        ("glyphs-synthetic", 360, 170, Synthetic),
    };

    public static void Sizes(DrawingContext dc)
    {
        float y = 4;
        foreach (var size in new[] { 9f, 10f, 11f, 12f, 13f, 14f, 16f, 20f, 24f, 32f, 48f, 72f })
        {
            y += size * 1.1f;
            var text = size >= 72 ? $"{size}px Hamburg" : size >= 32 ? $"{size}px Hamburg ÅÉ" : $"{size}px Hamburgefonstiv 0123 ÅÉñ";
            var run = Run(text, size);
            dc.DrawGlyphRun(run, new Point(6, MathF.Round(y)), C("#212121"));
            y += size * 0.25f;
        }
    }

    public static void Subpixel(DrawingContext dc)
    {
        // 每行起点的小数部分不同：子像素 x 量化到 1/4 px，y 对齐到整像素
        for (int i = 0; i < 8; i++)
        {
            float x = 6 + i * 0.125f;
            float y = 16 + i * 16 + i * 0.3f;
            dc.DrawGlyphRun(Run("Illinois lilt |||| 1111 mm", 13), new Point(x, y), C("#000000"));
        }
        // 竖线阵列：每根 'l' 右移 0.25 px
        var glyphs = new ushort[16];
        var positions = new Point[16];
        Noto.TryGetGlyph('l', out var l);
        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphs[i] = l;
            positions[i] = new Point(i * 6.25f, 0);
        }
        dc.DrawGlyphRun(new GlyphRun(Noto, 20, glyphs, positions), new Point(200, 40), C("#0d47a1"));
        dc.DrawGlyphRun(new GlyphRun(Noto, 20, glyphs, positions), new Point(200.5f, 80), C("#b71c1c"));
    }

    public static void PaintScene(DrawingContext dc)
    {
        dc.DrawGlyphRun(Run("Solid colour text", 22), new Point(8, 28), C("#c62828"));
        dc.DrawGlyphRun(Run("Translucent text", 22), new Point(8, 56), C("#1565c080"));

        // 渐变：一个画刷横跨整串字形
        var run = Run("Gradient filled", 30);
        dc.DrawGlyphRun(run, new Point(8, 96), new LinearGradientBrush(new Point(8, 0), new Point(8 + run.Bounds.Right, 0),
            Stops((0, "#ff512f"), (0.5f, "#8e24aa"), (1, "#1e88e5"))));
        dc.DrawGlyphRun(Run("Radial", 34), new Point(240, 96),
            new RadialGradientBrush(new Point(290, 84), 50, Stops((0, "#ffeb3b"), (1, "#e65100"))));

        // 深色背景上的浅色文字
        dc.FillRect(new Rect(0, 108, 360, 44), C("#263238"));
        dc.DrawGlyphRun(Run("Light on dark 14px", 14), new Point(8, 126), C("#eceff1"));
        dc.DrawGlyphRun(Run("Light on dark 11px", 11), new Point(180, 126), C("#ffffff"));
        dc.DrawGlyphRun(Run("Yellow 18px", 18), new Point(8, 146), C("#ffd54f"));

        // 混合模式
        dc.FillRect(new Rect(0, 156, 360, 44),
            new LinearGradientBrush(new Point(0, 0), new Point(360, 0), Stops((0, "#ff9800"), (1, "#3f51b5"))));
        dc.DrawGlyphRun(Run("Multiply", 26), new Point(8, 188), new Paint(C("#4caf50"), BlendMode.Multiply));
        dc.DrawGlyphRun(Run("Difference", 26), new Point(170, 188), new Paint(C("#ffffff"), BlendMode.Difference));
    }

    public static void Transforms(DrawingContext dc)
    {
        // 旋转：回退为路径填充
        for (int i = 0; i < 4; i++)
        {
            dc.Save();
            dc.Translate(8, 130);
            dc.Transform(Matrix3x2.CreateRotation(-i * MathF.PI / 7));
            dc.DrawGlyphRun(Run("Rotated text", 15), new Point(30, 0), Color.FromRgba((byte)(60 * i), 90, (byte)(200 - 40 * i), 255));
            dc.Restore();
        }
        // 非均匀缩放
        dc.Save();
        dc.Translate(150, 40);
        dc.Transform(Matrix3x2.CreateScale(2, 1));
        dc.DrawGlyphRun(Run("Wide", 20), new Point(0, 0), C("#2e7d32"));
        dc.Restore();
        // 均匀缩放：仍走掩码缓存（设备字号 = 字号 × 缩放）
        dc.Save();
        dc.Translate(150, 80);
        dc.Transform(Matrix3x2.CreateScale(1.5f));
        dc.DrawGlyphRun(Run("Scaled 1.5x", 16), new Point(0, 0), C("#4527a0"));
        dc.Restore();
        // 错切
        dc.Save();
        dc.Translate(150, 120);
        dc.Transform(new Matrix3x2(1, 0, -0.4f, 1, 0, 0));
        dc.DrawGlyphRun(Run("Skewed", 22), new Point(0, 0), C("#ad1457"));
        dc.Restore();
        // 竖排（旋转 90°）
        dc.Save();
        dc.Translate(330, 20);
        dc.Transform(Matrix3x2.CreateRotation(MathF.PI / 2));
        dc.DrawGlyphRun(Run("Vertical 90°", 18), new Point(0, 0), C("#00695c"));
        dc.Restore();
        // 超大字号：直接填充路径
        dc.DrawGlyphRun(Run("Big", 140), new Point(10, 250), C("#ff6f0040"));
        // 翻转（负缩放）：回退为路径填充，与上方正常绘制的镜像对称
        dc.DrawGlyphRun(Run("Flip", 20), new Point(250, 218), C("#000000"));
        dc.Save();
        dc.Translate(250, 222);
        dc.Transform(Matrix3x2.CreateScale(1, -1));
        dc.DrawGlyphRun(Run("Flip", 20), new Point(0, 0), C("#37474f"));
        dc.Restore();
    }

    public static void ClipAndLayers(DrawingContext dc)
    {
        // 半像素矩形裁剪（抗锯齿裁剪蒙版）
        dc.Save();
        dc.ClipRect(new Rect(10.5f, 8, 130, 22.5f));
        dc.DrawGlyphRun(Run("Clipped by a rectangle", 22), new Point(4, 28), C("#1a237e"));
        dc.Restore();
        dc.StrokeRect(new Rect(10.5f, 8, 130, 22.5f), C("#90caf9"), new Stroke(0.5f));

        // 圆形裁剪
        dc.Save();
        dc.ClipPath(VectorPath.FromEllipse(new Rect(170, 4, 120, 80)));
        for (int i = 0; i < 6; i++)
            dc.DrawGlyphRun(Run("clip clip clip clip", 14), new Point(160, 16 + i * 14), C("#6a1b9a"));
        dc.Restore();

        // 组透明度：重叠处不应更深
        dc.PushLayer(new LayerOptions(0.5f));
        dc.DrawGlyphRun(Run("Layer", 40), new Point(10, 120), C("#d32f2f"));
        dc.DrawGlyphRun(Run("Layer", 40), new Point(22, 128), C("#d32f2f"));
        dc.PopLayer();
        // 对比：逐个半透明
        dc.DrawGlyphRun(Run("Alpha", 40), new Point(150, 120), C("#d32f2f80"));
        dc.DrawGlyphRun(Run("Alpha", 40), new Point(162, 128), C("#d32f2f80"));

        // 带边界的图层 + 混合模式
        dc.FillRect(new Rect(10, 150, 340, 44), C("#ffe082"));
        dc.PushLayer(new LayerOptions(0.8f, BlendMode.Multiply, new Rect(20, 152, 200, 40)));
        dc.DrawGlyphRun(Run("Layer bounds + multiply", 24), new Point(14, 182), C("#0277bd"));
        dc.PopLayer();
    }

    public static void Synthetic(DrawingContext dc)
    {
        var rows = new[]
        {
            (GlyphRunFlags.None, "Regular"),
            (GlyphRunFlags.SyntheticBold, "Synthetic bold"),
            (GlyphRunFlags.SyntheticOblique, "Synthetic oblique"),
            (GlyphRunFlags.SyntheticBold | GlyphRunFlags.SyntheticOblique, "Bold oblique"),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            float y = 26 + i * 34;
            dc.DrawGlyphRun(Run(rows[i].Item2 + " Hag", 22, rows[i].Item1), new Point(8, y), C("#212121"));
            dc.DrawGlyphRun(Run("12px text", 12, rows[i].Item1), new Point(260, y), C("#212121"));
        }
        // 合成样式在回退路径（旋转）下也生效
        dc.Save();
        dc.Translate(250, 160);
        dc.Transform(Matrix3x2.CreateRotation(-0.1f));
        dc.DrawGlyphRun(Run("Bold+oblique", 14, GlyphRunFlags.SyntheticBold | GlyphRunFlags.SyntheticOblique), new Point(0, 0), C("#bf360c"));
        dc.Restore();
    }
}

public class GoldenTextSceneTests
{
    public static IEnumerable<object[]> SceneNames => TextScenes.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Text_scene_matches_golden(string name)
    {
        var scene = TextScenes.All.Single(s => s.Name == name);
        var dc = new DrawingContext();
        scene.Draw(dc);
        using var bmp = Golden.Render(dc.Finish(), scene.Width, scene.Height);
        Golden.AssertMatches(name, bmp);
    }

    [Fact]
    public void HiDpi_text_matches_golden()
    {
        var dc = new DrawingContext();
        TextScenes.PaintScene(dc);
        using var bmp = Golden.Render(dc.Finish(), 360, 200, scale: 2);
        Golden.AssertMatches("glyphs-paint@2x", bmp);
    }
}
