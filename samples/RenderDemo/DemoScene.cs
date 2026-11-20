using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI;
using AwAUI.Graphics;

namespace RenderDemo;

/// <summary>
/// 用 DrawingContext 手绘的界面草图：没有元素树与 CSS（那是 M1），这里验证的是绘制抽象与软件光栅器。
/// </summary>
public sealed class DemoScene
{
    public Point? Pointer;
    public bool Pressed;
    public int Clicks;
    public double Time;

    readonly List<(Rect Bounds, string Id)> _hitTargets = new();
    public string? Hovered { get; private set; }

    static Color C(string hex) => Color.Parse(hex);

    readonly Bitmap _avatar = new(new PixelSize(16, 16));
    int _avatarFrame = -1;

    /// <summary>按 <see cref="Time"/> 以 12 fps 改写头像位图（同一张位图，改写后 <see cref="Bitmap.Invalidate"/>）。</summary>
    void UpdateAvatar()
    {
        int frame = (int)Math.Floor(Time * 12);
        if (frame == _avatarFrame) return;
        _avatarFrame = frame;
        for (int y = 0; y < 16; y++)
        {
            var row = _avatar.GetRow(y);
            for (int x = 0; x < 16; x++)
            {
                // 同心的彩色环，随帧向外扩散；预乘 RGBA
                float d = MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (y - 7.5f) * (y - 7.5f));
                float t = (d - frame * 0.5f) * 0.6f;
                float a = d < 8 ? 1 : 0;
                row[x * 4] = (byte)(255 * a * (0.5f + 0.5f * MathF.Sin(t)));
                row[x * 4 + 1] = (byte)(255 * a * (0.5f + 0.5f * MathF.Sin(t + 2.1f)));
                row[x * 4 + 2] = (byte)(255 * a * (0.5f + 0.5f * MathF.Sin(t + 4.2f)));
                row[x * 4 + 3] = (byte)(255 * a);
            }
        }
        _avatar.Invalidate();
    }

    public bool IsAnimating => true; // 加载指示器一直在转

    public DisplayList Build(Size size)
    {
        _hitTargets.Clear();
        var dc = new DrawingContext();

        // 背景
        dc.FillRect(new Rect(0, 0, size.Width, size.Height),
            new LinearGradientBrush(new Point(0, 0), new Point(0, size.Height),
                new[] { new GradientStop(0, C("#eef2f7")), new GradientStop(1, C("#dfe6ef")) }));

        // 卡片居中
        float cw = MathF.Min(420, size.Width - 40), ch = 300;
        float cx = (size.Width - cw) / 2, cy = MathF.Max(20, (size.Height - ch) / 2);
        var card = new RoundRect(new Rect(cx, cy, cw, ch), 16);
        dc.DrawBoxShadow(card, new BoxShadow(0, 12, 32, -4, C("#1a2b4a33")));
        dc.DrawBoxShadow(card, new BoxShadow(0, 2, 6, 0, C("#1a2b4a1f")));
        dc.FillRoundRect(card, C("#ffffff"));

        // 头部渐变 + 圆角裁剪
        dc.Save();
        dc.ClipRoundRect(card);
        dc.FillRect(new Rect(cx, cy, cw, 96),
            new LinearGradientBrush(new Point(cx, cy), new Point(cx + cw, cy + 96),
                new[] { new GradientStop(0, C("#4f46e5")), new GradientStop(1, C("#06b6d4")) }));
        // 头部装饰圆
        dc.FillCircle(new Point(cx + cw - 40, cy + 10), 60, C("#ffffff1f"));
        dc.FillCircle(new Point(cx + cw - 110, cy + 90), 34, C("#ffffff14"));
        dc.Restore();

        // 头像
        var avatarCenter = new Point(cx + 56, cy + 96);
        dc.FillCircle(avatarCenter, 34, C("#ffffff"));
        dc.FillCircle(avatarCenter, 30,
            new RadialGradientBrush(new Point(avatarCenter.X - 8, avatarCenter.Y - 10), 36,
                new[] { new GradientStop(0, C("#fbbf24")), new GradientStop(1, C("#f97316")) }));
        // 头像上的像素画（DrawImage）：16×16 的位图每帧改写并递增版本（与动画 GIF 复用同一张位图相同），
        // 放大约 3.5 倍、圆形裁剪、双线性采样
        UpdateAvatar();
        dc.Save();
        dc.ClipRoundRect(new RoundRect(new Rect(avatarCenter.X - 30, avatarCenter.Y - 30, 60, 60), 30));
        dc.DrawImage(_avatar, new Rect(avatarCenter.X - 28, avatarCenter.Y - 28, 56, 56), ImageSampling.Linear, 0.85f);
        dc.Restore();

        // 文本占位条（文字渲染在 M1）
        dc.FillRoundRect(new RoundRect(new Rect(cx + 24, cy + 146, 180, 14), 7), C("#1f2937"));
        dc.FillRoundRect(new RoundRect(new Rect(cx + 24, cy + 170, cw - 48, 10), 5), C("#d1d5db"));
        dc.FillRoundRect(new RoundRect(new Rect(cx + 24, cy + 188, cw - 120, 10), 5), C("#d1d5db"));

        // 统计小图：折线 + 面积
        var chart = new Rect(cx + 24, cy + 210, cw - 48 - 140, 60);
        var line = new PathBuilder();
        var area = new PathBuilder().MoveTo(chart.Left, chart.Bottom);
        for (int i = 0; i <= 12; i++)
        {
            float x = chart.Left + chart.Width * i / 12;
            float y = chart.Bottom - 8 - (float)(0.5 + 0.5 * Math.Sin(i * 0.8 + Time * 1.5)) * (chart.Height - 16);
            if (i == 0) line.MoveTo(x, y); else line.LineTo(x, y);
            area.LineTo(x, y);
        }
        area.LineTo(chart.Right, chart.Bottom).Close();
        dc.FillPath(area.ToPath(), new LinearGradientBrush(new Point(0, chart.Top), new Point(0, chart.Bottom),
            new[] { new GradientStop(0, C("#4f46e540")), new GradientStop(1, C("#4f46e500")) }));
        dc.StrokePath(line.ToPath(), C("#4f46e5"), new Stroke(2, StrokeCap.Round, StrokeJoin.Round));

        // 按钮（悬停 / 按下状态）
        var button = new Rect(cx + cw - 24 - 120, cy + 226, 120, 40);
        Button(dc, button, "primary", C("#4f46e5"), C("#4338ca"), C("#3730a3"));

        // 计数徽章：每次点击多一个点
        for (int i = 0; i < Math.Min(Clicks, 12); i++)
            dc.FillCircle(new Point(cx + 24 + i * 14, cy + 290), 4, C("#10b981"));

        // 加载指示器（动画）
        Spinner(dc, new Point(cx + cw - 32, cy + 32), 12);

        // 指针跟随的光圈
        if (Pointer is { } p)
        {
            dc.FillCircle(p, 18, new RadialGradientBrush(p, 18,
                new[] { new GradientStop(0, C("#06b6d455")), new GradientStop(1, C("#06b6d400")) }));
        }

        return dc.Finish();
    }

    void Button(DrawingContext dc, Rect r, string id, Color normal, Color hover, Color pressed)
    {
        _hitTargets.Add((r, id));
        bool isHover = Hovered == id;
        var color = isHover ? (Pressed ? pressed : hover) : normal;
        var rr = new RoundRect(r, 10);
        if (!Pressed || !isHover) dc.DrawBoxShadow(rr, new BoxShadow(0, isHover ? 6 : 3, isHover ? 14 : 8, -2, C("#4f46e566")));
        dc.FillRoundRect(rr, color);
        // 「文字」：一个箭头图标
        var c = r.Center;
        var arrow = new PathBuilder().MoveTo(c.X - 10, c.Y).LineTo(c.X + 8, c.Y)
            .MoveTo(c.X + 2, c.Y - 6).LineTo(c.X + 8, c.Y).LineTo(c.X + 2, c.Y + 6).ToPath();
        dc.StrokePath(arrow, C("#ffffff"), new Stroke(2.5f, StrokeCap.Round, StrokeJoin.Round));
    }

    void Spinner(DrawingContext dc, Point center, float radius)
    {
        dc.Save();
        dc.Translate(center.X, center.Y);
        dc.Transform(Matrix3x2.CreateRotation((float)(Time * 2 * Math.PI)));
        dc.StrokePath(new PathBuilder().AddArc(Point.Zero, radius, radius, 0, 2 * MathF.PI).ToPath(), C("#ffffff55"), new Stroke(3));
        dc.StrokePath(new PathBuilder().AddArc(Point.Zero, radius, radius, 0, MathF.PI * 0.6f).ToPath(), C("#ffffff"),
            new Stroke(3, StrokeCap.Round));
        dc.Restore();
    }

    /// <summary>根据指针位置更新悬停目标，返回是否发生变化。</summary>
    public bool UpdateHover()
    {
        string? hit = null;
        if (Pointer is { } p)
            foreach (var (bounds, id) in _hitTargets)
                if (bounds.Contains(p)) hit = id;
        bool changed = hit != Hovered;
        Hovered = hit;
        return changed;
    }
}
