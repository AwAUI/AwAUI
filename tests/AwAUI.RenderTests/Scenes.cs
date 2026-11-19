using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.RenderTests;

/// <summary>黄金图像用的参考场景（design/13 的「一致性场景」雏形）。每个场景同时也是示例程序的素材。</summary>
public static class Scenes
{
    static Color C(string hex) => Color.Parse(hex);
    static GradientStop[] Stops(params (float, string)[] s) => s.Select(x => new GradientStop(x.Item1, C(x.Item2))).ToArray();

    public static readonly (string Name, int Width, int Height, Action<DrawingContext> Draw)[] All =
    {
        ("shapes", 240, 160, Shapes),
        ("fill-rules", 240, 120, FillRules),
        ("strokes", 300, 220, Strokes),
        ("gradients", 300, 160, Gradients),
        ("clip-layers", 300, 160, ClipAndLayers),
        ("transforms", 260, 200, Transforms),
        ("box-shadows", 320, 160, BoxShadows),
        ("blend-modes", 300, 90, BlendModes),
        ("gradients-focal", 300, 160, FocalGradients),
        ("layer-isolation", 300, 90, LayerIsolation),
    };

    public static void Shapes(DrawingContext dc)
    {
        dc.FillRect(new Rect(10, 10, 60, 40), C("#e53935"));
        dc.FillRect(new Rect(80.5f, 10.5f, 60, 40), C("#43a047"));               // 半像素对齐
        dc.FillRoundRect(new RoundRect(new Rect(150, 10, 80, 40), 12), C("#1e88e5"));
        dc.FillRoundRect(new RoundRect(new Rect(10, 60, 100, 40), 20, 0, 20, 0), C("#8e24aa"));
        dc.FillRoundRect(new RoundRect(new Rect(120, 60, 110, 40),
            new Size(40, 20), new Size(10, 10), new Size(40, 20), new Size(10, 10)), C("#fb8c00"));
        dc.FillCircle(new Point(40, 130), 22, C("#00897b"));
        dc.FillEllipse(new Rect(75, 112, 70, 36), C("#6d4c41"));
        // 半透明叠加
        dc.FillCircle(new Point(180, 128), 22, C("#ff000099"));
        dc.FillCircle(new Point(200, 128), 22, C("#0000ff99"));
        // 极细矩形
        dc.FillRect(new Rect(150, 104, 80, 0.5f), C("#000000"));
    }

    public static void FillRules(DrawingContext dc)
    {
        VectorPath Star(float cx, float cy, float r)
        {
            var pb = new PathBuilder();
            for (int i = 0; i < 5; i++)
            {
                float a = -MathF.PI / 2 + i * 4 * MathF.PI / 5;
                var p = new Point(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
                if (i == 0) pb.MoveTo(p); else pb.LineTo(p);
            }
            return pb.Close().ToPath();
        }
        dc.FillPath(Star(60, 60, 50), C("#3949ab"));
        dc.FillPath(Star(180, 60, 50).WithFillRule(FillRule.EvenOdd), C("#3949ab"));
        // 甜甜圈：奇偶规则下内圆为洞
        var donut = new PathBuilder { FillRule = FillRule.EvenOdd };
        donut.AddCircle(new Point(120, 95), 20).AddCircle(new Point(120, 95), 10);
        dc.FillPath(donut.ToPath(), C("#d81b60"));
    }

    public static void Strokes(DrawingContext dc)
    {
        var zig = new PathBuilder().MoveTo(0, 30).LineTo(25, 0).LineTo(50, 30).LineTo(75, 5).ToPath();
        var joins = new[] { StrokeJoin.Miter, StrokeJoin.Round, StrokeJoin.Bevel };
        for (int i = 0; i < 3; i++)
        {
            dc.Save();
            dc.Translate(15 + i * 95, 15);
            dc.StrokePath(zig, C("#37474f"), new Stroke(10, StrokeCap.Butt, joins[i]));
            dc.StrokePath(zig, C("#ff5252"), new Stroke(1));
            dc.Restore();
        }
        var caps = new[] { StrokeCap.Butt, StrokeCap.Round, StrokeCap.Square };
        for (int i = 0; i < 3; i++)
        {
            float y = 80 + i * 22;
            dc.DrawLine(new Point(25, y), new Point(125, y), C("#00796b"), new Stroke(12, caps[i]));
            dc.DrawLine(new Point(25, y), new Point(125, y), C("#ffffff"), new Stroke(1));
        }
        dc.DrawLine(new Point(150, 80), new Point(285, 80), C("#5e35b1"), new Stroke(4, dashes: new[] { 12f, 6f }));
        dc.DrawLine(new Point(150, 100), new Point(285, 100), C("#5e35b1"), new Stroke(4, StrokeCap.Round, dashes: new[] { 0f, 10f }));
        dc.StrokeRoundRect(new RoundRect(new Rect(155, 115, 125, 30), 10), C("#f4511e"), new Stroke(3, dashes: new[] { 8f, 4f }));
        // 细线：0.5 / 1 / 2 像素
        for (int i = 0; i < 3; i++)
        {
            float w = new[] { 0.5f, 1f, 2f }[i];
            dc.DrawLine(new Point(15, 165 + i * 10), new Point(140, 160 + i * 10), C("#000000"), new Stroke(w));
        }
        // 闭合三角形 + 圆角描边
        var tri = new PathBuilder().MoveTo(200, 160).LineTo(270, 160).LineTo(235, 210).Close().ToPath();
        dc.StrokePath(tri, C("#1565c0"), new Stroke(8, join: StrokeJoin.Round));
        // 曲线描边
        var curve = new PathBuilder().MoveTo(150, 205).CubicTo(new Point(160, 150), new Point(185, 230), new Point(195, 175)).ToPath();
        dc.StrokePath(curve, C("#2e7d32"), new Stroke(3, StrokeCap.Round));
    }

    public static void Gradients(DrawingContext dc)
    {
        dc.FillRoundRect(new RoundRect(new Rect(10, 10, 130, 60), 8),
            new LinearGradientBrush(new Point(10, 0), new Point(140, 0), Stops((0, "#ff512f"), (1, "#dd2476"))));
        dc.FillRoundRect(new RoundRect(new Rect(160, 10, 130, 60), 8),
            new LinearGradientBrush(new Point(160, 10), new Point(200, 50), Stops((0, "#00c6ff"), (1, "#0072ff")), SpreadMode.Reflect));
        dc.FillCircle(new Point(45, 115), 35,
            new RadialGradientBrush(new Point(35, 105), 40, Stops((0, "#ffffff"), (0.3f, "#ffeb3b"), (1, "#ff6f00"))));
        dc.FillRect(new Rect(95, 80, 90, 70),
            new RadialGradientBrush(new Point(140, 115), 12, Stops((0, "#311b92"), (1, "#b388ff")), SpreadMode.Repeat));
        dc.FillCircle(new Point(245, 115), 35,
            new ConicGradientBrush(new Point(245, 115), 0, Stops((0, "#f44336"), (0.33f, "#ffeb3b"), (0.66f, "#2196f3"), (1, "#f44336"))));
        // 到透明的渐变：预乘插值下不应出现灰边
        dc.FillRect(new Rect(10, 150, 280, 6),
            new LinearGradientBrush(new Point(10, 0), new Point(290, 0), Stops((0, "#ff0000ff"), (1, "#0000ff00"))));
    }

    /// <summary>带焦点的径向渐变（双点锥形）：焦点在内、起始圆半径、焦点恰在圆上、焦点在外（锥形，锥外透明）、扩展模式与变换。</summary>
    public static void FocalGradients(DrawingContext dc)
    {
        var sun = Stops((0, "#ffffff"), (0.25f, "#ffeb3b"), (0.7f, "#ff6f00"), (1, "#4e342e"));
        // 焦点在圆内（SVG 的 fx/fy）
        dc.FillCircle(new Point(45, 45), 35, new RadialGradientBrush(new Point(45, 45), 35, new Point(28, 30), 0, sun));
        // 起始圆有半径：t = 0 的圆内是第一个色标
        dc.FillRect(new Rect(90, 10, 70, 70),
            new RadialGradientBrush(new Point(125, 45), 34, new Point(115, 40), 8, Stops((0, "#1a237e"), (0.5f, "#42a5f5"), (1, "#e3f2fd"))));
        // 焦点恰在结束圆上（a = 0，一次方程）
        dc.FillRect(new Rect(170, 10, 60, 70),
            new RadialGradientBrush(new Point(200, 45), 28, new Point(172, 45), 0, Stops((0, "#004d40"), (1, "#a7ffeb"))));
        // 焦点在结束圆外：锥形，锥外不绘制（透出背景）
        dc.FillRect(new Rect(238, 10, 56, 70), C("#eeeeee"));
        dc.FillRect(new Rect(238, 10, 56, 70),
            new RadialGradientBrush(new Point(278, 45), 14, new Point(242, 40), 3, Stops((0, "#d50000"), (1, "#ffd600"))));
        // 重复与反射扩展
        dc.FillRect(new Rect(10, 90, 90, 60),
            new RadialGradientBrush(new Point(55, 120), 12, new Point(50, 117), 2, Stops((0, "#311b92"), (1, "#b388ff")), SpreadMode.Repeat));
        dc.FillRect(new Rect(110, 90, 90, 60),
            new RadialGradientBrush(new Point(155, 120), 15, new Point(165, 125), 0, Stops((0, "#880e4f"), (0.6f, "#f48fb1"), (1, "#fce4ec")), SpreadMode.Reflect));
        // 渐变空间变换 + 旋转的绘制变换（GPU 走掩码 + 着色器取色）
        dc.Save();
        dc.Translate(250, 120);
        dc.Transform(Matrix3x2.CreateRotation(0.4f));
        dc.FillRect(new Rect(-38, -26, 76, 52),
            new RadialGradientBrush(new Point(0, 0), 30, new Point(-12, 6), 4, Stops((0, "#fff176"), (1, "#1b5e20")),
                SpreadMode.Pad, Matrix3x2.CreateScale(1.4f, 0.8f)));
        dc.Restore();
    }

    /// <summary>
    /// 图层隔离：不透明度 1 的 SrcOver 图层里的 DstOut / DstIn / Clear 只作用于图层自己的内容，父表面上的内容不受影响
    /// （design/13 的 PushLayer 是隔离组）。
    /// </summary>
    public static void LayerIsolation(DrawingContext dc)
    {
        for (int i = 0; i < 6; i++) dc.FillRect(new Rect(i * 50, 0, 25, 90), C("#e53935"));
        // DstOut 在图层里挖洞：洞里是父表面的条纹，而不是透明
        dc.PushLayer(new LayerOptions(1f));
        dc.FillRoundRect(new RoundRect(new Rect(10, 10, 80, 70), 10), C("#1e88e5"));
        dc.FillCircle(new Point(50, 45), 22, new Paint(C("#000000"), BlendMode.DstOut));
        dc.PopLayer();
        // 半透明源的 DstIn：图层内容减半后再盖到父表面
        dc.PushLayer(new LayerOptions(1f));
        dc.FillRect(new Rect(105, 10, 80, 70), C("#43a047"));
        dc.FillRect(new Rect(105, 10, 80, 70), new Paint(new LinearGradientBrush(new Point(105, 0), new Point(185, 0),
            Stops((0, "#000000ff"), (1, "#00000000"))), BlendMode.DstIn));
        dc.PopLayer();
        // 嵌套 DisplayList 里的 Clear，外层是带 Multiply 子图层的不透明图层
        var child = new DrawingContext();
        child.FillRect(new Rect(225, 30, 30, 30), new Paint(C("#000000"), BlendMode.Clear));
        dc.PushLayer(new LayerOptions(1f));
        dc.FillRect(new Rect(200, 10, 80, 70), C("#fdd835"));
        dc.DrawDisplayList(child.Finish());
        dc.PushLayer(new LayerOptions(1f, BlendMode.Multiply));
        dc.FillCircle(new Point(270, 70), 16, C("#8e24aa"));
        dc.PopLayer();
        dc.PopLayer();
    }

    public static void ClipAndLayers(DrawingContext dc)
    {
        // 圆角裁剪内的条纹
        dc.Save();
        dc.ClipRoundRect(new RoundRect(new Rect(10, 10, 130, 90), 24));
        for (int i = 0; i < 12; i++)
            dc.FillRect(new Rect(10 + i * 12, 10, 6, 90), C(i % 2 == 0 ? "#26a69a" : "#004d40"));
        dc.Restore();

        // 嵌套裁剪：矩形 ∩ 圆
        dc.Save();
        dc.ClipRect(new Rect(160, 10, 60, 90));
        dc.ClipPath(VectorPath.FromEllipse(new Rect(170, 15, 90, 80)));
        dc.FillRect(new Rect(150, 0, 150, 120), C("#7e57c2"));
        dc.Restore();

        // 组透明度：重叠处不应更深
        dc.PushLayer(new LayerOptions(0.5f));
        dc.FillCircle(new Point(50, 130), 22, C("#e53935"));
        dc.FillCircle(new Point(80, 130), 22, C("#e53935"));
        dc.PopLayer();
        // 对比：逐个半透明
        dc.FillCircle(new Point(140, 130), 22, C("#e5393580"));
        dc.FillCircle(new Point(170, 130), 22, C("#e5393580"));

        // 带边界的图层 + 裁剪
        dc.PushLayer(new LayerOptions(0.7f, BlendMode.SrcOver, new Rect(210, 105, 80, 50)));
        dc.FillRoundRect(new RoundRect(new Rect(200, 100, 100, 60), 10), C("#1e88e5"));
        dc.PopLayer();
    }

    public static void Transforms(DrawingContext dc)
    {
        // 旋转
        for (int i = 0; i < 6; i++)
        {
            dc.Save();
            dc.Translate(60, 60);
            dc.Transform(Matrix3x2.CreateRotation(i * MathF.PI / 12));
            dc.FillRect(new Rect(-30, -4, 60, 8), Color.FromRgba((byte)(40 * i), 120, (byte)(255 - 40 * i), 200));
            dc.Restore();
        }
        // 非均匀缩放下的描边（线宽随之变形）
        dc.Save();
        dc.Translate(130, 20);
        dc.Transform(Matrix3x2.CreateScale(3, 1));
        dc.StrokeRect(new Rect(0, 0, 35, 70), C("#c62828"), new Stroke(3));
        dc.Restore();

        // 子 DisplayList 复用
        var child = new DrawingContext();
        child.FillRoundRect(new RoundRect(new Rect(0, 0, 30, 30), 6), C("#43a047"));
        child.FillCircle(new Point(15, 15), 6, C("#ffffff"));
        var tile = child.Finish();
        for (int i = 0; i < 4; i++)
        {
            dc.Save();
            dc.Translate(20 + i * 40, 140);
            dc.Transform(Matrix3x2.CreateRotation(i * 0.2f, new Vector2(15, 15)));
            dc.DrawDisplayList(tile);
            dc.Restore();
        }
        // 错切
        dc.Save();
        dc.Transform(new Matrix3x2(1, 0, -0.5f, 1, 0, 0));
        dc.FillRect(new Rect(260, 130, 40, 50), C("#ffb300"));
        dc.Restore();
    }

    public static void BoxShadows(DrawingContext dc)
    {
        var card = new RoundRect(new Rect(20, 20, 120, 80), 12);
        dc.DrawBoxShadow(card, new BoxShadow(0, 6, 16, 0, C("#00000060")));
        dc.FillRoundRect(card, C("#ffffff"));

        var spread = new RoundRect(new Rect(180, 20, 110, 50), 6);
        dc.DrawBoxShadow(spread, new BoxShadow(0, 0, 0, 6, C("#2196f3")));
        dc.FillRoundRect(spread, C("#ffffff"));

        var inset = new RoundRect(new Rect(180, 95, 110, 50), 25);
        dc.FillRoundRect(inset, C("#eceff1"));
        dc.DrawBoxShadow(inset, new BoxShadow(3, 3, 8, 0, C("#00000080"), inset: true));

        var sharp = new RoundRect(new Rect(30, 120, 100, 24), 0);
        dc.DrawBoxShadow(sharp, new BoxShadow(4, 4, 0, 0, C("#e91e63")));
        dc.FillRoundRect(sharp, C("#ffffff"));
    }

    public static void BlendModes(DrawingContext dc)
    {
        var modes = new[] { BlendMode.Multiply, BlendMode.Screen, BlendMode.Overlay, BlendMode.Difference, BlendMode.Plus };
        for (int i = 0; i < modes.Length; i++)
        {
            float x = 10 + i * 58;
            dc.FillRect(new Rect(x, 10, 50, 70),
                new LinearGradientBrush(new Point(x, 0), new Point(x + 50, 0), Stops((0, "#ff9800"), (1, "#3f51b5"))));
            dc.FillCircle(new Point(x + 25, 45), 20, new Paint(C("#4caf50"), modes[i]));
        }
    }
}

public class GoldenSceneTests
{
    public static IEnumerable<object[]> SceneNames => Scenes.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Scene_matches_golden(string name)
    {
        var scene = Scenes.All.Single(s => s.Name == name);
        var dc = new DrawingContext();
        scene.Draw(dc);
        using var bmp = Golden.Render(dc.Finish(), scene.Width, scene.Height);
        Golden.AssertMatches(name, bmp);
    }

    [Fact]
    public void HiDpi_render_matches_golden()
    {
        var dc = new DrawingContext();
        Scenes.Shapes(dc);
        using var bmp = Golden.Render(dc.Finish(), 240, 160, scale: 2);
        Golden.AssertMatches("shapes@2x", bmp);
    }
}
