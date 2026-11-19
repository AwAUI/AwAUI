using System;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Svg.Rendering;

/// <summary>路径几何辅助：精确包围盒（曲线取极值点）、仿射变换、长度上界。</summary>
internal static class PathGeometry
{
    /// <summary>几何包围盒（SVG 的 bbox：曲线按真实极值，而非控制点）。</summary>
    public static Rect TightBounds(VectorPath path)
    {
        if (path.IsEmpty) return Rect.Empty;
        var pts = path.Points;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        void Add(float x, float y)
        {
            if (x < l) l = x;
            if (x > r) r = x;
            if (y < t) t = y;
            if (y > b) b = y;
        }
        int pi = 0;
        Point cur = default;
        Span<float> roots = stackalloc float[4];
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.Move:
                case PathVerb.Line:
                    cur = pts[pi++];
                    Add(cur.X, cur.Y);
                    break;
                case PathVerb.Quad:
                {
                    Point c = pts[pi], p = pts[pi + 1];
                    pi += 2;
                    Add(p.X, p.Y);
                    QuadExtrema(cur.X, c.X, p.X, out float tx);
                    QuadExtrema(cur.Y, c.Y, p.Y, out float ty);
                    if (tx > 0 && tx < 1) { var q = EvalQuad(cur, c, p, tx); Add(q.X, q.Y); }
                    if (ty > 0 && ty < 1) { var q = EvalQuad(cur, c, p, ty); Add(q.X, q.Y); }
                    cur = p;
                    break;
                }
                case PathVerb.Cubic:
                {
                    Point c1 = pts[pi], c2 = pts[pi + 1], p = pts[pi + 2];
                    pi += 3;
                    Add(p.X, p.Y);
                    int n = CubicExtrema(cur.X, c1.X, c2.X, p.X, roots, 0);
                    n = CubicExtrema(cur.Y, c1.Y, c2.Y, p.Y, roots, n);
                    for (int i = 0; i < n; i++)
                    {
                        var q = EvalCubic(cur, c1, c2, p, roots[i]);
                        Add(q.X, q.Y);
                    }
                    cur = p;
                    break;
                }
            }
        }
        return l <= r ? Rect.FromLTRB(l, t, r, b) : Rect.Empty;
    }

    static void QuadExtrema(float a, float b, float c, out float t)
    {
        float d = a - 2 * b + c;
        t = d != 0 ? (a - b) / d : -1;
    }

    static int CubicExtrema(float p0, float p1, float p2, float p3, Span<float> roots, int n)
    {
        // B'(t)/3 = (p1-p0)(1-t)² + 2(p2-p1)t(1-t) + (p3-p2)t²
        double a = -p0 + 3 * p1 - 3 * p2 + p3;
        double b = 2 * (p0 - 2 * p1 + p2);
        double c = p1 - p0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12) n = AddRoot(-c / b, roots, n);
            return n;
        }
        double disc = b * b - 4 * a * c;
        if (disc < 0) return n;
        double sq = Math.Sqrt(disc);
        n = AddRoot((-b + sq) / (2 * a), roots, n);
        n = AddRoot((-b - sq) / (2 * a), roots, n);
        return n;
    }

    static int AddRoot(double t, Span<float> roots, int n)
    {
        if (t > 0 && t < 1 && n < roots.Length) roots[n++] = (float)t;
        return n;
    }

    static Point EvalQuad(Point p0, Point c, Point p1, float t)
    {
        float u = 1 - t;
        return new Point(u * u * p0.X + 2 * u * t * c.X + t * t * p1.X, u * u * p0.Y + 2 * u * t * c.Y + t * t * p1.Y);
    }

    static Point EvalCubic(Point p0, Point c1, Point c2, Point p1, float t)
    {
        float u = 1 - t;
        float a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new Point(a * p0.X + b * c1.X + c * c2.X + d * p1.X, a * p0.Y + b * c1.Y + c * c2.Y + d * p1.Y);
    }

    /// <summary>把路径按 <paramref name="m"/> 变换后追加到 <paramref name="builder"/>（仿射变换保持贝塞尔曲线）。</summary>
    public static void AppendTransformed(PathBuilder builder, VectorPath path, in Matrix3x2 m)
    {
        var pts = path.Points;
        int pi = 0;
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.Move: builder.MoveTo(pts[pi++].Transform(m)); break;
                case PathVerb.Line: builder.LineTo(pts[pi++].Transform(m)); break;
                case PathVerb.Quad: builder.QuadTo(pts[pi].Transform(m), pts[pi + 1].Transform(m)); pi += 2; break;
                case PathVerb.Cubic: builder.CubicTo(pts[pi].Transform(m), pts[pi + 1].Transform(m), pts[pi + 2].Transform(m)); pi += 3; break;
                case PathVerb.Close: builder.Close(); break;
            }
        }
    }

    /// <summary>与光栅器相同的展平段数估计（Wang 公式，容差 0.05 像素，每条曲线至多 1000 段）。</summary>
    public static long FlattenCost(VectorPath path, double scale)
    {
        const double Tolerance = 0.05;
        var pts = path.Points;
        long cost = 0;
        Point cur = default;
        int pi = 0;
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.Move:
                case PathVerb.Line:
                    cur = pts[pi++];
                    cost++;
                    break;
                case PathVerb.Quad:
                {
                    Point c = pts[pi], p = pts[pi + 1];
                    pi += 2;
                    double dd = Len(cur.X - 2.0 * c.X + p.X, cur.Y - 2.0 * c.Y + p.Y);
                    cost += Segments(Math.Sqrt(dd * scale / (8 * Tolerance)));
                    cur = p;
                    break;
                }
                case PathVerb.Cubic:
                {
                    Point c1 = pts[pi], c2 = pts[pi + 1], p = pts[pi + 2];
                    pi += 3;
                    double dd = Math.Max(Len(cur.X - 2.0 * c1.X + c2.X, cur.Y - 2.0 * c1.Y + c2.Y),
                        Len(c1.X - 2.0 * c2.X + p.X, c1.Y - 2.0 * c2.Y + p.Y));
                    cost += Segments(Math.Sqrt(0.75 * dd * scale / Tolerance));
                    cur = p;
                    break;
                }
                default:
                    cost++;
                    break;
            }
        }
        return cost;

        static double Len(double x, double y) => Math.Sqrt(x * x + y * y);
        static long Segments(double n) => n >= 1000 || double.IsNaN(n) ? 1000 : Math.Max(1, (long)Math.Ceiling(n));
    }

    /// <summary>把所有曲线替换为连接其端点的直线（弦）。</summary>
    public static VectorPath ToChords(VectorPath path)
    {
        var pb = new PathBuilder { FillRule = path.FillRule };
        var pts = path.Points;
        int pi = 0;
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.Move: pb.MoveTo(pts[pi++]); break;
                case PathVerb.Line: pb.LineTo(pts[pi++]); break;
                case PathVerb.Quad: pb.LineTo(pts[pi + 1]); pi += 2; break;
                case PathVerb.Cubic: pb.LineTo(pts[pi + 2]); pi += 3; break;
                case PathVerb.Close: pb.Close(); break;
            }
        }
        return pb.ToPath();
    }

    /// <summary>路径长度的上界（控制多边形总长），用于估计虚线段数。</summary>
    public static double LengthUpperBound(VectorPath path)
    {
        var pts = path.Points;
        double len = 0;
        Point start = default, prev = default;
        int pi = 0;
        foreach (var v in path.Verbs)
        {
            int n = v switch { PathVerb.Move or PathVerb.Line => 1, PathVerb.Quad => 2, PathVerb.Cubic => 3, _ => 0 };
            if (v == PathVerb.Move)
            {
                start = prev = pts[pi++];
                continue;
            }
            if (v == PathVerb.Close)
            {
                len += Dist(prev, start);
                prev = start;
                continue;
            }
            for (int i = 0; i < n; i++)
            {
                len += Dist(prev, pts[pi]);
                prev = pts[pi++];
            }
        }
        return len;
    }

    static double Dist(Point a, Point b)
    {
        double dx = a.X - (double)b.X, dy = a.Y - (double)b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
