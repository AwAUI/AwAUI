using System;

namespace AwAUI;

/// <summary>设备像素尺寸。</summary>
public readonly struct PixelSize : IEquatable<PixelSize>
{
    public PixelSize(int width, int height) { Width = width; Height = height; }
    public int Width { get; }
    public int Height { get; }
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelSize FromSize(Size size, double scale) =>
        new((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));

    public static bool operator ==(PixelSize a, PixelSize b) => a.Equals(b);
    public static bool operator !=(PixelSize a, PixelSize b) => !a.Equals(b);
    public bool Equals(PixelSize o) => Width == o.Width && Height == o.Height;
    public override bool Equals(object? obj) => obj is PixelSize s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(Width, Height);
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>设备像素矩形（半开区间 [Left, Right) × [Top, Bottom)）。</summary>
public readonly struct PixelRect : IEquatable<PixelRect>
{
    public PixelRect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelSize Size => new(Width, Height);

    public static PixelRect FromLTRB(int l, int t, int r, int b) => new(l, t, r - l, b - t);

    /// <summary>覆盖给定浮点矩形的最小像素矩形。</summary>
    public static PixelRect RoundOut(Rect r)
    {
        if (r.IsEmpty || !r.IsFinite) return default;
        return FromLTRB(Floor(r.Left), Floor(r.Top), Ceil(r.Right), Ceil(r.Bottom));
    }

    // 两侧都钳制：坐标 ≥ 2^31 的有限矩形在 (int) 转换时溢出（net8 x64 / netfx 上得到 int.MinValue）
    static int Floor(float v) => (int)Clamp(Math.Floor(v));
    static int Ceil(float v) => (int)Clamp(Math.Ceiling(v));
    static double Clamp(double v) => Math.Min(int.MaxValue / 2, Math.Max(int.MinValue / 2, v));

    public PixelRect Intersect(PixelRect o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? FromLTRB(l, t, r, b) : default;
    }

    public PixelRect Union(PixelRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        return FromLTRB(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    }

    public Rect ToRect() => new(X, Y, Width, Height);

    public static bool operator ==(PixelRect a, PixelRect b) => a.Equals(b);
    public static bool operator !=(PixelRect a, PixelRect b) => !a.Equals(b);
    public bool Equals(PixelRect o) => X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
    public override bool Equals(object? obj) => obj is PixelRect r && Equals(r);
    public override int GetHashCode() => HashCodeCompat.Combine(X, Y, Width, Height);
    public override string ToString() => $"[{X}, {Y}, {Width}x{Height}]";
}
