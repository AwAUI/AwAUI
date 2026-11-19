using System;

namespace AwAUI;

/// <summary>逻辑像素尺寸。</summary>
public readonly struct Size : IEquatable<Size>
{
    public Size(float width, float height) { Width = width; Height = height; }

    public float Width { get; }
    public float Height { get; }

    public static Size Empty => default;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static bool operator ==(Size a, Size b) => a.Equals(b);
    public static bool operator !=(Size a, Size b) => !a.Equals(b);

    public bool Equals(Size other) => Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => obj is Size s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(Width.GetHashCode(), Height.GetHashCode());
    public override string ToString() => FormattableString.Invariant($"{Width}x{Height}");
}
