using System;
using System.Numerics;

namespace AwAUI.Render.Raster;

/// <summary>
/// 一组多边形轮廓（点序列，隐式闭合）。光栅化的输入，也是展平与描边的输出。
/// </summary>
internal sealed class PolyList
{
    Vector2[] _points = new Vector2[64];
    int[] _starts = new int[8];

    public int PointCount { get; private set; }
    public int ContourCount { get; private set; }

    public ReadOnlySpan<Vector2> Points => new(_points, 0, PointCount);

    public void Clear()
    {
        PointCount = 0;
        ContourCount = 0;
    }

    public void BeginContour()
    {
        // 空轮廓直接复用
        if (ContourCount > 0 && _starts[ContourCount - 1] == PointCount) return;
        if (ContourCount == _starts.Length) Array.Resize(ref _starts, _starts.Length * 2);
        _starts[ContourCount++] = PointCount;
    }

    public void Add(Vector2 p)
    {
        if (ContourCount == 0) BeginContour();
        if (PointCount == _points.Length) Array.Resize(ref _points, _points.Length * 2);
        _points[PointCount++] = p;
    }

    public ReadOnlySpan<Vector2> Contour(int index)
    {
        int start = _starts[index];
        int end = index + 1 < ContourCount ? _starts[index + 1] : PointCount;
        return new ReadOnlySpan<Vector2>(_points, start, end - start);
    }

    public void Transform(in Matrix3x2 m)
    {
        if (m.IsIdentity) return;
        for (int i = 0; i < PointCount; i++) _points[i] = Vector2.Transform(_points[i], m);
    }

    public void AppendFrom(PolyList other)
    {
        for (int c = 0; c < other.ContourCount; c++)
        {
            BeginContour();
            foreach (var p in other.Contour(c)) Add(p);
        }
    }

    /// <summary>追加 <paramref name="other"/> 的全部轮廓，并平移 <paramref name="offset"/>。<paramref name="other"/> 不能是自身。</summary>
    public void AppendFrom(PolyList other, Vector2 offset)
    {
        for (int c = 0; c < other.ContourCount; c++)
        {
            BeginContour();
            foreach (var p in other.Contour(c)) Add(p + offset);
        }
    }

    /// <summary>
    /// 全部点的包围盒。没有点、或任何坐标不是有限值（变换溢出得到的 ∞、∞ − ∞ = NaN 等）时返回 false，调用方不绘制：
    /// NaN 不参与比较，包围盒仍会是有限的，而后续光栅化会因此越界。
    /// </summary>
    public bool TryGetBounds(out float left, out float top, out float right, out float bottom)
    {
        left = top = float.MaxValue;
        right = bottom = float.MinValue;
        for (int i = 0; i < PointCount; i++)
        {
            var p = _points[i];
            if (!(Math.Abs(p.X) <= float.MaxValue) || !(Math.Abs(p.Y) <= float.MaxValue))
            {
                left = top = right = bottom = 0;
                return false;
            }
            if (p.X < left) left = p.X;
            if (p.X > right) right = p.X;
            if (p.Y < top) top = p.Y;
            if (p.Y > bottom) bottom = p.Y;
        }
        return PointCount > 0;
    }
}
