using System;
using System.Collections.Generic;

namespace AwAUI.Graphics;

/// <summary>可变的路径构建器。</summary>
public sealed class PathBuilder
{
    /// <summary>用三次贝塞尔近似四分之一圆弧的控制点系数。</summary>
    internal const float Kappa = 0.5522847498f;

    readonly List<PathVerb> _verbs = new();
    readonly List<Point> _points = new();
    Point _current;
    Point _subpathStart;
    bool _hasSubpath;

    public FillRule FillRule { get; set; }

    public PathBuilder MoveTo(float x, float y) => MoveTo(new Point(x, y));
    public PathBuilder LineTo(float x, float y) => LineTo(new Point(x, y));

    public PathBuilder MoveTo(Point p)
    {
        // 连续的 MoveTo 只保留最后一个
        if (_verbs.Count > 0 && _verbs[_verbs.Count - 1] == PathVerb.Move)
            _points[_points.Count - 1] = p;
        else
        {
            _verbs.Add(PathVerb.Move);
            _points.Add(p);
        }
        _current = _subpathStart = p;
        _hasSubpath = true;
        return this;
    }

    void EnsureSubpath()
    {
        if (!_hasSubpath) MoveTo(_current);
    }

    public PathBuilder LineTo(Point p)
    {
        EnsureSubpath();
        _verbs.Add(PathVerb.Line);
        _points.Add(p);
        _current = p;
        return this;
    }

    public PathBuilder QuadTo(Point c, Point p)
    {
        EnsureSubpath();
        _verbs.Add(PathVerb.Quad);
        _points.Add(c);
        _points.Add(p);
        _current = p;
        return this;
    }

    public PathBuilder CubicTo(Point c1, Point c2, Point p)
    {
        EnsureSubpath();
        _verbs.Add(PathVerb.Cubic);
        _points.Add(c1);
        _points.Add(c2);
        _points.Add(p);
        _current = p;
        return this;
    }

    public PathBuilder Close()
    {
        if (_hasSubpath && _verbs.Count > 0 && _verbs[_verbs.Count - 1] != PathVerb.Close)
            _verbs.Add(PathVerb.Close);
        _current = _subpathStart;
        _hasSubpath = false;
        return this;
    }

    public PathBuilder AddRect(Rect r)
    {
        MoveTo(r.Left, r.Top).LineTo(r.Right, r.Top).LineTo(r.Right, r.Bottom).LineTo(r.Left, r.Bottom);
        return Close();
    }

    public PathBuilder AddRoundRect(in RoundRect rr)
    {
        if (rr.IsRect) return AddRect(rr.Rect);
        var r = rr.Rect;
        Size tl = rr.TopLeft, tr = rr.TopRight, br = rr.BottomRight, bl = rr.BottomLeft;
        float k = Kappa;
        MoveTo(r.Left + tl.Width, r.Top);
        LineTo(r.Right - tr.Width, r.Top);
        if (!tr.IsEmpty)
            CubicTo(new Point(r.Right - tr.Width * (1 - k), r.Top), new Point(r.Right, r.Top + tr.Height * (1 - k)), new Point(r.Right, r.Top + tr.Height));
        LineTo(r.Right, r.Bottom - br.Height);
        if (!br.IsEmpty)
            CubicTo(new Point(r.Right, r.Bottom - br.Height * (1 - k)), new Point(r.Right - br.Width * (1 - k), r.Bottom), new Point(r.Right - br.Width, r.Bottom));
        LineTo(r.Left + bl.Width, r.Bottom);
        if (!bl.IsEmpty)
            CubicTo(new Point(r.Left + bl.Width * (1 - k), r.Bottom), new Point(r.Left, r.Bottom - bl.Height * (1 - k)), new Point(r.Left, r.Bottom - bl.Height));
        LineTo(r.Left, r.Top + tl.Height);
        if (!tl.IsEmpty)
            CubicTo(new Point(r.Left, r.Top + tl.Height * (1 - k)), new Point(r.Left + tl.Width * (1 - k), r.Top), new Point(r.Left + tl.Width, r.Top));
        return Close();
    }

    public PathBuilder AddEllipse(Rect r)
    {
        float cx = r.Center.X, cy = r.Center.Y, rx = r.Width / 2, ry = r.Height / 2;
        float kx = rx * Kappa, ky = ry * Kappa;
        MoveTo(cx + rx, cy);
        CubicTo(new Point(cx + rx, cy + ky), new Point(cx + kx, cy + ry), new Point(cx, cy + ry));
        CubicTo(new Point(cx - kx, cy + ry), new Point(cx - rx, cy + ky), new Point(cx - rx, cy));
        CubicTo(new Point(cx - rx, cy - ky), new Point(cx - kx, cy - ry), new Point(cx, cy - ry));
        CubicTo(new Point(cx + kx, cy - ry), new Point(cx + rx, cy - ky), new Point(cx + rx, cy));
        return Close();
    }

    public PathBuilder AddCircle(Point center, float radius) =>
        AddEllipse(new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));

    /// <summary>
    /// 添加一段椭圆弧（角度为弧度，顺时针为正，即 y 向下坐标系中的视觉顺时针）。
    /// <paramref name="connect"/> 为 true 时用直线连接当前点，否则开始新子路径。
    /// </summary>
    public PathBuilder AddArc(Point center, float rx, float ry, float startAngle, float sweepAngle, bool connect = false)
    {
        var start = new Point(center.X + rx * MathF.Cos(startAngle), center.Y + ry * MathF.Sin(startAngle));
        if (connect && _hasSubpath) LineTo(start); else MoveTo(start);
        int segments = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweepAngle) / (MathF.PI / 2) - 1e-4f));
        float step = sweepAngle / segments;
        float a0 = startAngle;
        for (int i = 0; i < segments; i++)
        {
            float a1 = a0 + step;
            float h = 4f / 3f * MathF.Tan((a1 - a0) / 4);
            float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0), c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);
            CubicTo(
                new Point(center.X + rx * (c0 - h * s0), center.Y + ry * (s0 + h * c0)),
                new Point(center.X + rx * (c1 + h * s1), center.Y + ry * (s1 - h * c1)),
                new Point(center.X + rx * c1, center.Y + ry * s1));
            a0 = a1;
        }
        return this;
    }

    public PathBuilder AddPath(VectorPath path)
    {
        int pi = 0;
        var pts = path.Points;
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.Move: MoveTo(pts[pi++]); break;
                case PathVerb.Line: LineTo(pts[pi++]); break;
                case PathVerb.Quad: QuadTo(pts[pi], pts[pi + 1]); pi += 2; break;
                case PathVerb.Cubic: CubicTo(pts[pi], pts[pi + 1], pts[pi + 2]); pi += 3; break;
                case PathVerb.Close: Close(); break;
            }
        }
        return this;
    }

    public VectorPath ToPath()
    {
        // 去掉末尾孤立的 MoveTo
        var verbs = _verbs.ToArray();
        var points = _points.ToArray();
        if (verbs.Length > 0 && verbs[verbs.Length - 1] == PathVerb.Move)
        {
            Array.Resize(ref verbs, verbs.Length - 1);
            Array.Resize(ref points, points.Length - 1);
        }
        return new VectorPath(verbs, points, FillRule);
    }
}
