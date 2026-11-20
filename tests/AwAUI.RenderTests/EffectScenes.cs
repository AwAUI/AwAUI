using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.RenderTests;

/// <summary>
/// 第九批的效果场景：滤镜图层（CSS <c>filter</c>）、背景滤镜（<c>backdrop-filter</c>）与它们和变换、裁剪的组合。
/// 软件后端的黄金图像在 tests/TestFiles/Golden/effects-*.png；GPU 对照（Rhi.Metal.Tests）链接本文件。
/// </summary>
public static class EffectScenes
{
    static Color C(string hex) => Color.Parse(hex);
    static GradientStop[] Stops(params (float, string)[] s) => s.Select(x => new GradientStop(x.Item1, C(x.Item2))).ToArray();

    public static readonly (string Name, int Width, int Height, double Scale, Action<DrawingContext> Draw)[] All =
    {
        ("effects-color-filters", 360, 200, 1, ColorFilters),
        ("effects-blur", 320, 180, 1, Blurs),
        ("effects-blur@2x", 320, 180, 2, Blurs),
        ("effects-drop-shadow", 320, 160, 1, DropShadows),
        ("effects-filter-combos", 320, 200, 1, FilterCombos),
        ("effects-backdrop", 320, 200, 1, Backdrop),
        ("effects-backdrop@2x", 320, 200, 2, Backdrop),
    };

    /// <summary>一个色彩丰富的小样本：渐变、三原色块、半透明圆、细线。</summary>
    static void Swatch(DrawingContext dc, float x, float y)
    {
        dc.FillRect(new Rect(x, y, 70, 70), new LinearGradientBrush(new Point(x, y), new Point(x + 70, y + 70),
            Stops((0, "#ff5722"), (0.5f, "#ffeb3b"), (1, "#3f51b5"))));
        dc.FillRect(new Rect(x + 6, y + 6, 18, 18), C("#e53935"));
        dc.FillRect(new Rect(x + 26, y + 6, 18, 18), C("#43a047"));
        dc.FillRect(new Rect(x + 46, y + 6, 18, 18), C("#1e88e5"));
        dc.FillCircle(new Point(x + 35, y + 48), 16, C("#ffffffb0"));
        dc.FillRect(new Rect(x + 4, y + 62, 62, 2), C("#212121"));
    }

    public static void ColorFilters(DrawingContext dc)
    {
        var filters = new[]
        {
            FilterOperation.Brightness(1.4f), FilterOperation.Contrast(1.8f), FilterOperation.Grayscale(1), FilterOperation.Sepia(0.8f),
            FilterOperation.Saturate(2.5f), FilterOperation.HueRotate(120), FilterOperation.Invert(1), FilterOperation.Opacity(0.4f),
        };
        for (int i = 0; i < filters.Length; i++)
        {
            float x = 10 + (i % 4) * 88, y = 10 + (i / 4) * 95;
            dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(x, y, 70, 70), new Filter(filters[i])));
            Swatch(dc, x, y);
            dc.PopLayer();
        }
    }

    public static void Blurs(DrawingContext dc)
    {
        // 小 σ（直接卷积）
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(10, 10, 90, 70), new Filter(FilterOperation.Blur(2))));
        Swatch(dc, 20, 10);
        dc.PopLayer();
        // 大 σ（降采样路径）
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(120, 10, 80, 70), new Filter(FilterOperation.Blur(9))));
        Swatch(dc, 125, 10);
        dc.PopLayer();
        // 各向异性
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(220, 10, 80, 70), new Filter(FilterOperation.Blur(6, 0))));
        Swatch(dc, 225, 10);
        dc.PopLayer();
        // 模糊 + 组透明度，裁在圆角矩形内（滤镜的输出受外层裁剪）
        dc.Save();
        dc.ClipRoundRect(new RoundRect(new Rect(10, 100, 140, 70), 18));
        dc.PushLayer(new LayerOptions(0.7f, BlendMode.SrcOver, new Rect(20, 100, 120, 70), new Filter(FilterOperation.Blur(4))));
        Swatch(dc, 30, 100);
        dc.FillRect(new Rect(100, 110, 30, 50), C("#8e24aa"));
        dc.PopLayer();
        dc.Restore();
        // 旋转下的模糊
        dc.Save();
        dc.Translate(240, 140);
        dc.Transform(Matrix3x2.CreateRotation(0.5f));
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(-30, -20, 60, 40), new Filter(FilterOperation.Blur(3))));
        dc.FillRect(new Rect(-30, -20, 60, 40), C("#00897b"));
        dc.FillRect(new Rect(-10, -20, 8, 40), C("#ffffff"));
        dc.PopLayer();
        dc.Restore();
    }

    public static void DropShadows(DrawingContext dc)
    {
        dc.FillRect(new Rect(0, 0, 320, 160), C("#eceff1"));
        // 圆角卡片
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(20, 20, 90, 60), new Filter(FilterOperation.DropShadow(4, 6, 5, C("#00000080")))));
        dc.FillRoundRect(new RoundRect(new Rect(20, 20, 90, 60), 10), C("#ffffff"));
        dc.PopLayer();
        // 不规则形状（投影跟随 alpha 轮廓）
        var star = new PathBuilder();
        for (int i = 0; i < 10; i++)
        {
            float r = i % 2 == 0 ? 40 : 18;
            float a = -MathF.PI / 2 + i * MathF.PI / 5;
            var p = new Point(190 + r * MathF.Cos(a), 55 + r * MathF.Sin(a));
            if (i == 0) star.MoveTo(p); else star.LineTo(p);
        }
        star.Close();
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(150, 15, 80, 80), new Filter(FilterOperation.DropShadow(0, 0, 4, C("#ff5722")))));
        dc.FillPath(star.ToPath(), C("#ffc107"));
        dc.PopLayer();
        // 硬投影（σ = 0）与彩色投影，再叠一层灰度
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(250, 20, 50, 50),
            new Filter(FilterOperation.DropShadow(6, 6, 0, C("#3949ab")), FilterOperation.DropShadow(-4, 10, 2, C("#e91e63a0")))));
        dc.FillCircle(new Point(275, 45), 24, C("#26a69a"));
        dc.PopLayer();
        // 半透明内容的投影
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(30, 100, 260, 40), new Filter(FilterOperation.DropShadow(3, 3, 2, C("#000000")))));
        for (int i = 0; i < 8; i++) dc.FillRoundRect(new RoundRect(new Rect(30 + i * 33, 100, 26, 40), 6), Color.FromRgba((byte)(30 * i), 150, 220, (byte)(80 + 20 * i)));
        dc.PopLayer();
    }

    public static void FilterCombos(DrawingContext dc)
    {
        // 链：投影 → 灰度 → 轻微模糊
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(10, 10, 80, 80),
            new Filter(FilterOperation.DropShadow(5, 5, 3, C("#d50000")), FilterOperation.Grayscale(1), FilterOperation.Blur(1))));
        Swatch(dc, 15, 15);
        dc.PopLayer();
        // 嵌套的滤镜图层：外层色相旋转，内层模糊
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(110, 10, 90, 90), new Filter(FilterOperation.HueRotate(200))));
        Swatch(dc, 115, 15);
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(150, 50, 40, 40), new Filter(FilterOperation.Blur(3))));
        dc.FillRect(new Rect(155, 55, 30, 30), C("#ff1744"));
        dc.PopLayer();
        dc.PopLayer();
        // 缩放下的滤镜：σ 与投影偏移随变换缩放
        dc.Save();
        dc.Translate(220, 10);
        dc.Transform(Matrix3x2.CreateScale(1.5f));
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(0, 0, 50, 50), new Filter(FilterOperation.DropShadow(3, 3, 2, C("#00000099")))));
        dc.FillRoundRect(new RoundRect(new Rect(5, 5, 40, 40), 8), C("#7cb342"));
        dc.PopLayer();
        dc.Restore();
        // 路径裁剪内的滤镜（CSS 顺序：先滤镜后裁剪）
        dc.Save();
        dc.ClipPath(VectorPath.FromEllipse(new Rect(20, 110, 140, 80)));
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(20, 110, 140, 80), new Filter(FilterOperation.Blur(5), FilterOperation.Saturate(3))));
        for (int i = 0; i < 7; i++) dc.FillRect(new Rect(20 + i * 20, 110, 10, 80), C(i % 2 == 0 ? "#5c6bc0" : "#ffca28"));
        dc.PopLayer();
        dc.Restore();
        // 带混合模式的滤镜图层
        dc.FillRect(new Rect(180, 110, 130, 80), new LinearGradientBrush(new Point(180, 0), new Point(310, 0), Stops((0, "#00bcd4"), (1, "#ff4081"))));
        dc.PushLayer(new LayerOptions(0.9f, BlendMode.Multiply, new Rect(190, 120, 110, 60), new Filter(FilterOperation.Invert(1))));
        dc.FillRoundRect(new RoundRect(new Rect(195, 125, 100, 50), 12), C("#4caf50"));
        dc.PopLayer();
    }

    /// <summary>背景：条纹、文字般的细节与一个渐变圆，然后是几块毛玻璃。</summary>
    public static void Backdrop(DrawingContext dc)
    {
        for (int i = 0; i < 16; i++) dc.FillRect(new Rect(i * 20, 0, 10, 200), C(i % 3 == 0 ? "#e53935" : i % 3 == 1 ? "#1e88e5" : "#fdd835"));
        dc.FillCircle(new Point(160, 100), 70, new RadialGradientBrush(new Point(160, 100), 70, Stops((0, "#ffffff"), (1, "#4a148c"))));
        for (int i = 0; i < 12; i++) dc.FillRect(new Rect(20 + i * 24, 150, 16, 3), C("#000000"));

        // 毛玻璃（模糊 + 提亮），圆角
        var glass = new RoundRect(new Rect(20, 20, 120, 80), 14);
        dc.DrawBackdrop(glass, new Filter(FilterOperation.Blur(6), FilterOperation.Brightness(1.2f)));
        dc.FillRoundRect(glass, C("#ffffff40"));
        // 灰度背景（无模糊），半透明
        dc.DrawBackdrop(new RoundRect(new Rect(180, 20, 120, 60), 0), new Filter(FilterOperation.Grayscale(1)), 0.6f);
        // 旋转的背景模糊
        dc.Save();
        dc.Translate(230, 140);
        dc.Transform(Matrix3x2.CreateRotation(-0.3f));
        dc.DrawBackdrop(new RoundRect(new Rect(-50, -25, 100, 50), 10), new Filter(FilterOperation.Blur(3)));
        dc.StrokeRoundRect(new RoundRect(new Rect(-50, -25, 100, 50), 10), C("#ffffff"), new Stroke(1.5f));
        dc.Restore();
        // 半透明图层里的背景滤镜：读取的是图层自己的表面（backdrop root）
        dc.PushLayer(new LayerOptions(0.8f));
        dc.FillRect(new Rect(20, 120, 120, 70), C("#00acc1"));
        dc.FillRect(new Rect(40, 130, 20, 50), C("#ffffff"));
        dc.DrawBackdrop(new RoundRect(new Rect(30, 140, 90, 40), 8), new Filter(FilterOperation.Blur(4), FilterOperation.Invert(1)));
        dc.PopLayer();
    }
}

public class EffectGoldenTests
{
    public static IEnumerable<object[]> SceneNames => EffectScenes.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Effect_scene_matches_golden(string name)
    {
        var scene = EffectScenes.All.Single(s => s.Name == name);
        var dc = new DrawingContext();
        scene.Draw(dc);
        using var bmp = Golden.Render(dc.Finish(), scene.Width, scene.Height, scene.Scale);
        Golden.AssertMatches(name, bmp);
    }
}
