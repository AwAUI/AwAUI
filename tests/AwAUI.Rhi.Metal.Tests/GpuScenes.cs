using System.Numerics;
using AwAUI.Graphics;
using AwAUI.RenderTests;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>
/// 黄金图像场景没有覆盖到的 GPU 路径：解析描边（外圈 − 内圈）、不抗锯齿的解析形状、各种扩展模式的渐变、
/// 嵌套图层与读目标的混合、超深的图层嵌套、旋转下的盒阴影（CPU 回退）、裁剪蒙版与混合的组合。
/// 期望输出同样是软件后端。
/// </summary>
public static class GpuScenes
{
    static Color C(string hex) => Color.Parse(hex);
    static GradientStop[] Stops(params (float, string)[] s) => s.Select(x => new GradientStop(x.Item1, C(x.Item2))).ToArray();

    public static readonly (string Name, int Width, int Height, Action<DrawingContext> Draw)[] All =
    {
        ("ring-strokes", 320, 200, RingStrokes),
        ("aliased", 240, 120, Aliased),
        ("gradient-spreads", 320, 200, GradientSpreads),
        ("nested-layers", 320, 200, NestedLayers),
        ("deep-layers", 200, 120, DeepLayers),
        ("rotated-shadows", 320, 200, RotatedShadows),
        ("clip-blend", 300, 160, ClipBlend),
        ("many-paths", 400, 300, ManyPaths),
    };

    public static void RingStrokes(DrawingContext dc)
    {
        dc.StrokeRect(new Rect(10, 10, 80, 50), C("#1e88e5"), new Stroke(4));
        dc.StrokeRect(new Rect(100.5f, 10.25f, 80, 50), C("#e53935"), new Stroke(1));
        dc.StrokeRoundRect(new RoundRect(new Rect(190, 10, 110, 50), 12), C("#43a047"), new Stroke(6));
        dc.StrokeRoundRect(new RoundRect(new Rect(10, 80, 120, 50), 25), C("#8e24aa"), new Stroke(2.5f));
        // 半径小于半线宽：内圈是直角
        dc.StrokeRoundRect(new RoundRect(new Rect(150, 80, 70, 50), 3), C("#fb8c00"), new Stroke(10));
        // 混合了直角与圆角（直角处斜接）
        dc.StrokeRoundRect(new RoundRect(new Rect(240, 80, 60, 50), 15, 0, 15, 0), C("#00897b"), new Stroke(4));
        // 回退路径：椭圆角、圆角连接的直角、斜切连接
        dc.StrokeRoundRect(new RoundRect(new Rect(10, 150, 90, 40), new Size(30, 12), new Size(30, 12), new Size(30, 12), new Size(30, 12)), C("#6d4c41"), new Stroke(3));
        dc.StrokeRect(new Rect(120, 150, 70, 40), C("#3949ab"), new Stroke(8, join: StrokeJoin.Round));
        dc.StrokeRect(new Rect(210, 150, 70, 40), C("#d81b60"), new Stroke(8, join: StrokeJoin.Bevel));
        // 渐变描边
        dc.StrokeRoundRect(new RoundRect(new Rect(290, 150, 24, 40), 6), new LinearGradientBrush(new Point(290, 0), new Point(314, 0),
            Stops((0, "#ff512f"), (1, "#1e88e5"))), new Stroke(3));
    }

    public static void Aliased(DrawingContext dc)
    {
        var paint = new Paint(C("#263238"), antiAlias: false);
        dc.FillRect(new Rect(10.3f, 10.6f, 50.4f, 30.2f), paint);
        // 不抗锯齿时，边界恰好穿过像素中心是不稳定的平局（覆盖率正好 0.5），场景避开这种位置
        dc.FillRoundRect(new RoundRect(new Rect(70.3f, 10.2f, 60, 40), 14), paint);
        dc.StrokeRect(new Rect(140.5f, 10.5f, 80, 40), paint, new Stroke(3));
        dc.FillCircle(new Point(40, 85), 25, paint);
        dc.Save();
        dc.ClipRect(new Rect(80.4f, 60.4f, 70, 50), antiAlias: false);
        dc.FillRect(new Rect(60, 50, 120, 80), C("#ef6c00"));
        dc.Restore();
        dc.Save();
        dc.ClipRoundRect(new RoundRect(new Rect(160, 60, 70, 50), 20), antiAlias: false);
        dc.FillRect(new Rect(150, 50, 100, 80), C("#5e35b1"));
        dc.Restore();
    }

    public static void GradientSpreads(DrawingContext dc)
    {
        var spreads = new[] { SpreadMode.Pad, SpreadMode.Repeat, SpreadMode.Reflect };
        for (int i = 0; i < 3; i++)
        {
            float x = 10 + i * 105;
            dc.FillRect(new Rect(x, 10, 95, 50), new LinearGradientBrush(new Point(x + 30, 0), new Point(x + 50, 10),
                Stops((0, "#ff5252"), (0.5f, "#ffeb3b"), (1, "#448aff")), spreads[i]));
            dc.FillRoundRect(new RoundRect(new Rect(x, 70, 95, 60), 18), new RadialGradientBrush(new Point(x + 47, 100), 15,
                Stops((0, "#00000000"), (1, "#00c853")), spreads[i]));
        }
        // 带渐变空间变换的锥形渐变与半透明色标
        dc.FillRect(new Rect(10, 140, 140, 50), new ConicGradientBrush(new Point(80, 165), 0.7f,
            Stops((0, "#e91e63"), (0.5f, "#2196f380"), (1, "#e91e63")), Matrix3x2.CreateScale(1, 0.4f, new Vector2(80, 165))));
        // 零半径径向渐变、零长度线性渐变
        dc.FillRect(new Rect(160, 140, 70, 50), new RadialGradientBrush(new Point(195, 165), 0, Stops((0, "#ffffff"), (1, "#795548"))));
        dc.FillRect(new Rect(240, 140, 70, 50), new LinearGradientBrush(new Point(270, 0), new Point(270, 0), Stops((0, "#ff9800"), (1, "#3f51b5"))));
    }

    public static void NestedLayers(DrawingContext dc)
    {
        dc.FillRect(new Rect(0, 0, 320, 200), new LinearGradientBrush(new Point(0, 0), new Point(320, 0), Stops((0, "#ffe082"), (1, "#80deea"))));
        dc.PushLayer(new LayerOptions(0.8f));
        dc.FillRoundRect(new RoundRect(new Rect(20, 20, 140, 100), 16), C("#3949ab"));
        dc.PushLayer(new LayerOptions(0.6f, BlendMode.Screen));
        dc.FillCircle(new Point(110, 90), 50, C("#ff7043"));
        dc.PushLayer(new LayerOptions(1f, BlendMode.Multiply, new Rect(60, 60, 80, 60)));
        dc.FillRect(new Rect(40, 40, 140, 100), C("#66bb6a"));
        dc.PopLayer();
        dc.PopLayer();
        dc.PopLayer();
        // 兄弟图层（纹理复用）+ 每个图层内的读目标混合
        for (int i = 0; i < 6; i++)
        {
            dc.PushLayer(new LayerOptions(0.5f + i * 0.08f, i % 2 == 0 ? BlendMode.SrcOver : BlendMode.Overlay));
            dc.FillCircle(new Point(190 + i * 20, 60 + i * 15), 30, new Paint(C("#ab47bc"), i % 3 == 0 ? BlendMode.SrcOver : BlendMode.Difference));
            dc.PopLayer();
        }
        // 图层内裁剪后，合成只按图层内的裁剪范围写回
        dc.PushLayer(new LayerOptions(0.9f));
        dc.ClipRect(new Rect(30, 140, 60, 40));
        dc.FillRect(new Rect(0, 130, 200, 70), C("#d32f2f"));
        dc.PopLayer();
        // 透明度 0 的图层不绘制；不透明度 1 的 SrcOver 图层直接绘制
        dc.PushLayer(new LayerOptions(0f));
        dc.FillRect(new Rect(120, 140, 60, 40), C("#000000"));
        dc.PopLayer();
        dc.PushLayer(new LayerOptions(1f));
        dc.FillRect(new Rect(200, 140, 60, 40), C("#2e7d32"));
        dc.PopLayer();
    }

    /// <summary>超过 32 层的图层嵌套：更深的层直接画进父目标（两个后端都按降级处理时结果才一致，这里只用不透明度 1 的层）。</summary>
    public static void DeepLayers(DrawingContext dc)
    {
        for (int i = 0; i < 40; i++)
        {
            dc.PushLayer(new LayerOptions(i % 4 == 0 ? 0.97f : 1f, i % 7 == 3 ? BlendMode.Multiply : BlendMode.SrcOver));
            if (i < 30) dc.FillRect(new Rect(5 + i * 4, 5 + i * 2, 30, 30), Color.FromRgba((byte)(i * 6), 120, (byte)(255 - i * 6), 160));
        }
        for (int i = 0; i < 40; i++) dc.PopLayer();
    }

    public static void RotatedShadows(DrawingContext dc)
    {
        dc.Save();
        dc.Translate(90, 90);
        dc.Transform(Matrix3x2.CreateRotation(0.3f));
        var card = new RoundRect(new Rect(-50, -35, 100, 70), 12);
        dc.DrawBoxShadow(card, new BoxShadow(4, 8, 18, 2, C("#00000070")));
        dc.FillRoundRect(card, C("#ffffff"));
        dc.Restore();

        dc.Save();
        dc.Translate(240, 100);
        dc.Transform(Matrix3x2.CreateRotation(-0.2f));
        var inset = new RoundRect(new Rect(-55, -30, 110, 60), 20);
        dc.FillRoundRect(inset, C("#eceff1"));
        dc.DrawBoxShadow(inset, new BoxShadow(-3, 4, 10, 3, C("#1a237e90"), inset: true));
        dc.Restore();

        // 缩放下的解析阴影（σ 随缩放变化）与负扩展
        dc.Save();
        dc.Translate(20, 150);
        dc.Transform(Matrix3x2.CreateScale(1.5f, 1.5f));
        var small = new RoundRect(new Rect(0, 0, 60, 20), 6);
        dc.DrawBoxShadow(small, new BoxShadow(0, 3, 6, -2, C("#e91e6399")));
        dc.FillRoundRect(small, C("#fce4ec"));
        dc.Restore();
        var big = new RoundRect(new Rect(170, 160, 120, 24), new Size(20, 8), new Size(4, 4), new Size(20, 8), new Size(4, 4));
        dc.DrawBoxShadow(big, new BoxShadow(2, 2, 12, 0, C("#004d40a0")));
        dc.FillRoundRect(big, C("#e0f2f1"));
    }

    public static void ClipBlend(DrawingContext dc)
    {
        dc.FillRect(new Rect(0, 0, 300, 160), new LinearGradientBrush(new Point(0, 0), new Point(0, 160), Stops((0, "#fff3e0"), (1, "#e3f2fd"))));
        dc.Save();
        dc.ClipPath(VectorPath.FromEllipse(new Rect(20, 10, 160, 140)));
        dc.FillRect(new Rect(0, 0, 300, 160), new Paint(C("#ff9800"), BlendMode.Multiply));
        dc.ClipRoundRect(new RoundRect(new Rect(60, 30, 160, 100), 30));
        dc.FillCircle(new Point(120, 80), 50, new Paint(C("#3f51b5"), BlendMode.ColorDodge));
        dc.DrawGlyphRun(TextScenes.Run("Blend + clip", 22), new Point(70, 90), new Paint(C("#ffffff"), BlendMode.Difference));
        dc.Restore();
        // 不支持的混合模式按 SrcOver（两个后端一致地降级）
        dc.FillRect(new Rect(220, 20, 60, 60), new Paint(C("#00bcd4"), BlendMode.Hue));
        // 读目标混合里重叠的实例（逐个拷贝）
        for (int i = 0; i < 5; i++)
            dc.FillRoundRect(new RoundRect(new Rect(200 + i * 12, 95 + i * 6, 40, 40), 8), new Paint(C("#e91e6380"), BlendMode.Darken));
    }

    /// <summary>大量小路径（掩码图集的填充与跨页）。</summary>
    public static void ManyPaths(DrawingContext dc)
    {
        var rng = new Random(1234);
        for (int i = 0; i < 400; i++)
        {
            float x = (float)rng.NextDouble() * 380, y = (float)rng.NextDouble() * 280;
            float r = 2 + (float)rng.NextDouble() * 14;
            var color = Color.FromRgba((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)(80 + rng.Next(176)));
            if (i % 3 == 0) dc.FillCircle(new Point(x, y), r, color);
            else if (i % 3 == 1) dc.StrokePath(new PathBuilder().MoveTo(x, y).QuadTo(new Point(x + r, y - r), new Point(x + 2 * r, y)).ToPath(), color, new Stroke(1 + i % 4));
            else dc.FillRoundRect(new RoundRect(new Rect(x, y, r * 2, r), r * 0.4f), color);
        }
    }
}
