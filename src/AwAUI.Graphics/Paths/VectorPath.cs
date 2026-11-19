using System;
using System.Collections.Generic;

namespace AwAUI.Graphics;

public enum PathVerb : byte { Move, Line, Quad, Cubic, Close }

public enum FillRule : byte { NonZero, EvenOdd }

/// <summary>
/// 不可变的矢量路径：动词序列 + 控制点。由 <see cref="PathBuilder"/> 构建，可跨线程共享。
/// </summary>
public sealed class VectorPath
{
    readonly PathVerb[] _verbs;
    readonly Point[] _points;

    internal VectorPath(PathVerb[] verbs, Point[] points, FillRule fillRule)
    {
        _verbs = verbs;
        _points = points;
        FillRule = fillRule;
        Bounds = ComputeBounds(points);
    }

    public FillRule FillRule { get; }

    /// <summary>控制点的包围盒（对贝塞尔曲线是保守的）。</summary>
    public Rect Bounds { get; }

    public ReadOnlySpan<PathVerb> Verbs => _verbs;
    public ReadOnlySpan<Point> Points => _points;
    public bool IsEmpty => _verbs.Length == 0;

    /// <summary>得到一个只改变填充规则的副本（共享数据）。</summary>
    public VectorPath WithFillRule(FillRule rule) => rule == FillRule ? this : new VectorPath(_verbs, _points, rule);

    static Rect ComputeBounds(Point[] pts)
    {
        if (pts.Length == 0) return Rect.Empty;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < l) l = p.X;
            if (p.X > r) r = p.X;
            if (p.Y < t) t = p.Y;
            if (p.Y > b) b = p.Y;
        }
        return Rect.FromLTRB(l, t, r, b);
    }

    public static VectorPath FromRect(Rect r) => new PathBuilder().AddRect(r).ToPath();
    public static VectorPath FromRoundRect(in RoundRect rr) => new PathBuilder().AddRoundRect(rr).ToPath();
    public static VectorPath FromEllipse(Rect r) => new PathBuilder().AddEllipse(r).ToPath();

    /// <summary>逐段枚举（每个 Move 开始一个子路径）。</summary>
    public IEnumerable<(PathVerb Verb, Point[] Points)> Segments()
    {
        int pi = 0;
        foreach (var v in _verbs)
        {
            int n = v switch { PathVerb.Move => 1, PathVerb.Line => 1, PathVerb.Quad => 2, PathVerb.Cubic => 3, _ => 0 };
            var seg = new Point[n];
            Array.Copy(_points, pi, seg, 0, n);
            pi += n;
            yield return (v, seg);
        }
    }
}
