using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>一条展平后的折线。</summary>
internal sealed class Polyline
{
    public readonly List<Vector2> Points = new();
    public bool Closed;
}

/// <summary>轴对齐的可见范围（剔除用；坐标系由使用方决定：填充为设备像素，描边为局部坐标）。</summary>
internal readonly struct CullRect
{
    public CullRect(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly float Left, Top, Right, Bottom;

    /// <summary>设备像素矩形外扩 <paramref name="margin"/> 像素。</summary>
    public static CullRect FromPixels(PixelRect r, float margin) => new(r.X - margin, r.Y - margin, r.Right + margin, r.Bottom + margin);

    /// <summary>
    /// 设备像素矩形 <paramref name="device"/> 在局部坐标中的外接矩形（经 <paramref name="ctm"/> 的逆变换），再外扩局部坐标中的
    /// <paramref name="localMargin"/> 与一个设备像素。变换不可逆或结果不是有限值时返回 null（不剔除）。
    /// </summary>
    public static CullRect? FromDevice(PixelRect device, in Matrix3x2 ctm, float localMargin)
    {
        if (!Matrix3x2.Invert(ctm, out var inv)) return null;
        var p0 = Vector2.Transform(new Vector2(device.X, device.Y), inv);
        var p1 = Vector2.Transform(new Vector2(device.Right, device.Y), inv);
        var p2 = Vector2.Transform(new Vector2(device.X, device.Bottom), inv);
        var p3 = Vector2.Transform(new Vector2(device.Right, device.Bottom), inv);
        float margin = localMargin + 1f / MathF.Max(ctm.MaxScale(), 1e-6f);
        float l = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X)) - margin;
        float t = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y)) - margin;
        float r = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X)) + margin;
        float b = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y)) + margin;
        if (!(l <= r) || !(t <= b) || float.IsInfinity(l) || float.IsInfinity(r) || float.IsInfinity(t) || float.IsInfinity(b)) return null;
        return new CullRect(l, t, r, b);
    }

    public bool Contains(Vector2 p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    /// <summary>
    /// 点集是否整体落在范围某一侧之外（全部在左、右、上或下）。曲线的控制点凸包整体在外时，曲线本身也在外。
    /// 含 NaN 的点集返回 false（不剔除，交给后续的有限性检查）。
    /// </summary>
    public bool Excludes(Vector2 a, Vector2 b, Vector2 c, Vector2 d) =>
        a.X < Left && b.X < Left && c.X < Left && d.X < Left
        || a.X > Right && b.X > Right && c.X > Right && d.X > Right
        || a.Y < Top && b.Y < Top && c.Y < Top && d.Y < Top
        || a.Y > Bottom && b.Y > Bottom && c.Y > Bottom && d.Y > Bottom;
}

/// <summary>
/// 把贝塞尔曲线细分为折线。细分段数由几何误差上界决定。
/// <para>
/// 恶意输入的界限：一次展平最多产生 <see cref="MaxSubdivisionPoints"/> 个细分点（直线端点不计，它们与输入成正比），
/// 用完后剩余的曲线直接以弦代替；给出可见范围时，控制点凸包整体在范围一侧之外的曲线也以弦代替。
/// 弦与曲线之间的区域位于凸包内，所以对范围内的像素而言，填充的环绕数（以及描边的几何）不变。
/// </para>
/// </summary>
internal static class Flattener
{
    /// <summary>默认的设备像素容差。</summary>
    public const float DefaultTolerance = 0.05f;

    /// <summary>单条曲线的最大细分段数。</summary>
    const int MaxSegmentsPerCurve = 1000;

    /// <summary>一次展平最多产生的细分点数（约 8 MB 的点）。</summary>
    public const int MaxSubdivisionPoints = 1 << 20;

    interface IPointSink
    {
        void Add(Vector2 p);
    }

    readonly struct PolySink : IPointSink
    {
        readonly PolyList _list;
        public PolySink(PolyList list) => _list = list;
        public void Add(Vector2 p) => _list.Add(p);
    }

    readonly struct ListSink : IPointSink
    {
        readonly List<Vector2> _list;
        public ListSink(List<Vector2> list) => _list = list;
        public void Add(Vector2 p) => _list.Add(p);
    }

    /// <summary>
    /// 展平为多边形轮廓（用于填充）。先变换控制点再细分（仿射变换保持贝塞尔曲线）。
    /// <paramref name="cull"/> 为设备像素中的可见范围（应比实际绘制区域外扩至少 1 像素）。
    /// </summary>
    public static void FlattenToPolygons(VectorPath path, in Matrix3x2 transform, float tolerance, PolyList output, CullRect? cull = null)
    {
        var pts = path.Points;
        int pi = 0;
        int budget = MaxSubdivisionPoints;
        var sink = new PolySink(output);
        Vector2 current = default;
        foreach (var verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    output.BeginContour();
                    current = T(pts[pi++], transform);
                    output.Add(current);
                    break;
                case PathVerb.Line:
                    current = T(pts[pi++], transform);
                    output.Add(current);
                    break;
                case PathVerb.Quad:
                {
                    var c = T(pts[pi], transform); var p = T(pts[pi + 1], transform);
                    pi += 2;
                    if (cull is { } r && r.Excludes(current, c, c, p)) output.Add(p);
                    else Quad(current, c, p, tolerance, ref budget, sink);
                    current = p;
                    break;
                }
                case PathVerb.Cubic:
                {
                    var c1 = T(pts[pi], transform); var c2 = T(pts[pi + 1], transform); var p = T(pts[pi + 2], transform);
                    pi += 3;
                    if (cull is { } r && r.Excludes(current, c1, c2, p)) output.Add(p);
                    else Cubic(current, c1, c2, p, tolerance, ref budget, sink);
                    current = p;
                    break;
                }
                case PathVerb.Close:
                    break; // 多边形隐式闭合
            }
        }
    }

    /// <summary>
    /// 展平为折线（保留开闭信息，用于描边）。不做变换。<paramref name="cull"/> 为局部坐标中的可见范围，必须已按线宽、
    /// 连接与线帽外扩；虚线描边不能传（以弦代替曲线会改变弧长，从而改变可见部分的虚线相位）。
    /// </summary>
    public static List<Polyline> FlattenToPolylines(VectorPath path, float tolerance, CullRect? cull = null)
    {
        var result = new List<Polyline>();
        Polyline? current = null;
        Vector2 last = default, start = default;
        int budget = MaxSubdivisionPoints;

        // Close 之后不经 MoveTo 继续绘制时，从子路径起点开始一条新折线
        Polyline Current()
        {
            if (current == null)
            {
                current = new Polyline();
                current.Points.Add(start);
                result.Add(current);
            }
            return current;
        }

        var pts = path.Points;
        int pi = 0;
        foreach (var verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    last = start = pts[pi++];
                    current = null;
                    Current();
                    break;
                case PathVerb.Line:
                    last = pts[pi++];
                    Current().Points.Add(last);
                    break;
                case PathVerb.Quad:
                {
                    Vector2 c = pts[pi], p = pts[pi + 1];
                    pi += 2;
                    var points = Current().Points;
                    if (cull is { } r && r.Excludes(last, c, c, p)) points.Add(p);
                    else Quad(last, c, p, tolerance, ref budget, new ListSink(points));
                    last = p;
                    break;
                }
                case PathVerb.Cubic:
                {
                    Vector2 c1 = pts[pi], c2 = pts[pi + 1], p = pts[pi + 2];
                    pi += 3;
                    var points = Current().Points;
                    if (cull is { } r && r.Excludes(last, c1, c2, p)) points.Add(p);
                    else Cubic(last, c1, c2, p, tolerance, ref budget, new ListSink(points));
                    last = p;
                    break;
                }
                case PathVerb.Close:
                    if (current != null) current.Closed = true;
                    current = null;
                    last = start;
                    break;
            }
        }
        return result;
    }

    static Vector2 T(Point p, in Matrix3x2 m) => Vector2.Transform(new Vector2(p.X, p.Y), m);

    /// <summary>按剩余预算限制段数：内部点数 n − 1 不超过预算，用完后为 1 段（弦）。</summary>
    static int Limit(int n, ref int budget)
    {
        n = Math.Min(n, MaxSegmentsPerCurve);
        if (n - 1 > budget) n = budget + 1;
        budget -= n - 1;
        return n;
    }

    static void Quad<TSink>(Vector2 p0, Vector2 c, Vector2 p1, float tol, ref int budget, TSink add) where TSink : struct, IPointSink
    {
        // 二次曲线用 n 段折线近似的最大误差为 |p0 - 2c + p1| / (8 n²)
        float dd = (p0 - 2 * c + p1).Length();
        int n = Limit(Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dd / (8 * tol)))), ref budget);
        for (int i = 1; i < n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            add.Add(u * u * p0 + 2 * u * t * c + t * t * p1);
        }
        add.Add(p1);
    }

    static void Cubic<TSink>(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float tol, ref int budget, TSink add) where TSink : struct, IPointSink
    {
        // Wang 公式：n = ceil(sqrt(3/4 · max|二阶差分| / tol))
        float dd = Math.Max((p0 - 2 * c1 + c2).Length(), (c1 - 2 * c2 + p1).Length());
        int n = Limit(Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(0.75f * dd / tol))), ref budget);
        for (int i = 1; i < n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            add.Add(u * u * u * p0 + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * p1);
        }
        add.Add(p1);
    }
}
