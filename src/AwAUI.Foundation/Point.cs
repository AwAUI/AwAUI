using System;
using System.Numerics;

namespace AwAUI;

/// <summary>逻辑像素坐标中的点。x 向右、y 向下。</summary>
public readonly struct Point : IEquatable<Point>
{
    public Point(float x, float y) { X = x; Y = y; }

    public float X { get; }
    public float Y { get; }

    public static Point Zero => default;

    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
    public static Point operator *(Point a, float s) => new(a.X * s, a.Y * s);
    public static Point operator -(Point a) => new(-a.X, -a.Y);
    public static bool operator ==(Point a, Point b) => a.Equals(b);
    public static bool operator !=(Point a, Point b) => !a.Equals(b);

    public static implicit operator Vector2(Point p) => new(p.X, p.Y);
    public static implicit operator Point(Vector2 v) => new(v.X, v.Y);

    public float Length => MathF.Sqrt(X * X + Y * Y);

    public Point Transform(in Matrix3x2 m) => new(X * m.M11 + Y * m.M21 + m.M31, X * m.M12 + Y * m.M22 + m.M32);

    public bool Equals(Point other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is Point p && Equals(p);
    public override int GetHashCode() => HashCodeCompat.Combine(X.GetHashCode(), Y.GetHashCode());
    public override string ToString() => FormattableString.Invariant($"({X}, {Y})");
}
