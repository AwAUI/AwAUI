using System;
using System.Collections.Generic;

namespace AwAUI.Layout;

/// <summary>四条边上的样式值（margin、padding、border-width、inset）。构造函数参数顺序与 CSS 简写一致。</summary>
public readonly struct Edges<T> : IEquatable<Edges<T>> where T : struct, IEquatable<T>
{
    /// <summary>四边相同，如 <c>margin: 8px</c>。</summary>
    public Edges(T all) : this(all, all, all, all) { }

    /// <summary>如 <c>margin: 8px 16px</c>：上下、左右。</summary>
    public Edges(T vertical, T horizontal) : this(vertical, horizontal, vertical, horizontal) { }

    /// <summary>如 <c>margin: 1px 2px 3px</c>：上、左右、下。</summary>
    public Edges(T top, T horizontal, T bottom) : this(top, horizontal, bottom, horizontal) { }

    /// <summary>如 <c>margin: 1px 2px 3px 4px</c>：上、右、下、左（CSS 顺序）。</summary>
    public Edges(T top, T right, T bottom, T left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public T Top { get; init; }
    public T Right { get; init; }
    public T Bottom { get; init; }
    public T Left { get; init; }

    public bool Equals(Edges<T> o)
    {
        var c = EqualityComparer<T>.Default;
        return c.Equals(Top, o.Top) && c.Equals(Right, o.Right) && c.Equals(Bottom, o.Bottom) && c.Equals(Left, o.Left);
    }

    public override bool Equals(object? obj) => obj is Edges<T> e && Equals(e);
    public override int GetHashCode() =>
        HashCodeCompat.Combine(Top.GetHashCode(), Right.GetHashCode(), Bottom.GetHashCode(), Left.GetHashCode());
    public static bool operator ==(Edges<T> a, Edges<T> b) => a.Equals(b);
    public static bool operator !=(Edges<T> a, Edges<T> b) => !a.Equals(b);
    public override string ToString() => $"{Top} {Right} {Bottom} {Left}";
}

/// <summary>已解析为逻辑像素的四边宽度（布局结果中的 margin、padding、border）。</summary>
public readonly struct BoxEdges : IEquatable<BoxEdges>
{
    public BoxEdges(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public float Left { get; }
    public float Top { get; }
    public float Right { get; }
    public float Bottom { get; }

    /// <summary>左 + 右。</summary>
    public float Horizontal => Left + Right;
    /// <summary>上 + 下。</summary>
    public float Vertical => Top + Bottom;

    public static BoxEdges Zero => default;

    public static BoxEdges operator +(BoxEdges a, BoxEdges b) =>
        new(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);

    public bool Equals(BoxEdges o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;
    public override bool Equals(object? obj) => obj is BoxEdges e && Equals(e);
    public override int GetHashCode() =>
        HashCodeCompat.Combine(Left.GetHashCode(), Top.GetHashCode(), Right.GetHashCode(), Bottom.GetHashCode());
    public static bool operator ==(BoxEdges a, BoxEdges b) => a.Equals(b);
    public static bool operator !=(BoxEdges a, BoxEdges b) => !a.Equals(b);
    public override string ToString() => FormattableString.Invariant($"(L {Left}, T {Top}, R {Right}, B {Bottom})");
}
