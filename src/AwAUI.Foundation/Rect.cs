using System;
using System.Numerics;

namespace AwAUI;

/// <summary>逻辑像素矩形。宽高为负视为空。</summary>
public readonly struct Rect : IEquatable<Rect>
{
    public Rect(float x, float y, float width, float height)
    {
        X = x; Y = y; Width = width; Height = height;
    }

    public Rect(Point position, Size size) : this(position.X, position.Y, size.Width, size.Height) { }

    public float X { get; }
    public float Y { get; }
    public float Width { get; }
    public float Height { get; }

    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public Point TopLeft => new(X, Y);
    public Point TopRight => new(Right, Y);
    public Point BottomLeft => new(X, Bottom);
    public Point BottomRight => new(Right, Bottom);
    public Point Center => new(X + Width * 0.5f, Y + Height * 0.5f);
    public Size Size => new(Width, Height);

    public static Rect Empty => default;
    public bool IsEmpty => !(Width > 0 && Height > 0);
    public bool IsFinite =>
        !float.IsNaN(X) && !float.IsInfinity(X) && !float.IsNaN(Y) && !float.IsInfinity(Y) &&
        !float.IsNaN(Width) && !float.IsInfinity(Width) && !float.IsNaN(Height) && !float.IsInfinity(Height);

    public static Rect FromLTRB(float left, float top, float right, float bottom) =>
        new(left, top, right - left, bottom - top);

    public bool Contains(Point p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public Rect Inflate(float d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
    public Rect Inflate(float dx, float dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
    public Rect Offset(float dx, float dy) => new(X + dx, Y + dy, Width, Height);

    public Rect Intersect(Rect other)
    {
        float l = Math.Max(Left, other.Left), t = Math.Max(Top, other.Top);
        float r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r > l && b > t ? FromLTRB(l, t, r, b) : Empty;
    }

    public Rect Union(Rect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLTRB(Math.Min(Left, other.Left), Math.Min(Top, other.Top),
            Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public bool Intersects(Rect other) =>
        other.Left < Right && Left < other.Right && other.Top < Bottom && Top < other.Bottom;

    /// <summary>变换后的轴对齐包围盒（保守）。</summary>
    public Rect TransformBounds(in Matrix3x2 m)
    {
        if (m.IsIdentity) return this;
        var p0 = TopLeft.Transform(m); var p1 = TopRight.Transform(m);
        var p2 = BottomLeft.Transform(m); var p3 = BottomRight.Transform(m);
        float l = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        float r = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        float t = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        float b = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        return FromLTRB(l, t, r, b);
    }

    public static bool operator ==(Rect a, Rect b) => a.Equals(b);
    public static bool operator !=(Rect a, Rect b) => !a.Equals(b);

    public bool Equals(Rect o) => X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
    public override bool Equals(object? obj) => obj is Rect r && Equals(r);
    public override int GetHashCode() =>
        HashCodeCompat.Combine(X.GetHashCode(), Y.GetHashCode(), Width.GetHashCode(), Height.GetHashCode());
    public override string ToString() => FormattableString.Invariant($"[{X}, {Y}, {Width}x{Height}]");
}
