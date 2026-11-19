using System;

namespace AwAUI.Controls.Primitives;

/// <summary>四边的数值：碰撞内边距（Floating UI 的 <c>padding</c>）与溢出量（<c>detectOverflow</c> 的结果）共用。</summary>
public readonly struct SideValues : IEquatable<SideValues>
{
    public SideValues(float all) : this(all, all, all, all) { }

    public SideValues(float top, float right, float bottom, float left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public float Top { get; }
    public float Right { get; }
    public float Bottom { get; }
    public float Left { get; }

    public float this[Side side] => side switch
    {
        Side.Top => Top,
        Side.Right => Right,
        Side.Bottom => Bottom,
        _ => Left,
    };

    /// <summary>四边相同（<c>collisionPadding = 8</c>）。</summary>
    public static implicit operator SideValues(float all) => new(all);

    public bool Equals(SideValues o) => Top == o.Top && Right == o.Right && Bottom == o.Bottom && Left == o.Left;

    public override bool Equals(object? obj) => obj is SideValues o && Equals(o);

    public override int GetHashCode() => HashCodeCompat.Combine(Top.GetHashCode(), Right.GetHashCode(), Bottom.GetHashCode(), Left.GetHashCode());

    public static bool operator ==(SideValues a, SideValues b) => a.Equals(b);

    public static bool operator !=(SideValues a, SideValues b) => !a.Equals(b);

    public override string ToString() => FormattableString.Invariant($"{{top: {Top}, right: {Right}, bottom: {Bottom}, left: {Left}}}");
}

/// <summary>锚点（reference）与浮层（floating）的矩形。浮层矩形只用尺寸（位置恒为 0, 0，与 Floating UI 的 <c>getElementRects</c> 一致）。</summary>
public readonly struct ElementRects : IEquatable<ElementRects>
{
    public ElementRects(Rect reference, Size floating)
    {
        Reference = reference;
        Floating = new Rect(0, 0, floating.Width, floating.Height);
    }

    public Rect Reference { get; }

    public Rect Floating { get; }

    public bool Equals(ElementRects o) => Reference == o.Reference && Floating == o.Floating;

    public override bool Equals(object? obj) => obj is ElementRects o && Equals(o);

    public override int GetHashCode() => HashCodeCompat.Combine(Reference.GetHashCode(), Floating.GetHashCode());
}

/// <summary>溢出检测针对的元素（Floating UI 的 <c>elementContext</c>）。</summary>
public enum PopperElementContext : byte
{
    Floating,
    Reference,
}

/// <summary>
/// 定位所需的环境：根边界（视口）、两个元素各自的裁剪祖先求交后的矩形、书写方向。全部由宿主测量后给出，引擎本身不访问元素树。
/// <para>
/// 坐标系：所有矩形在同一坐标系中（通常是窗口客户区的逻辑像素）；输出的 x / y 也在这个坐标系里（相当于 Floating UI 的 <c>strategy: 'fixed'</c>）。
/// </para>
/// </summary>
public sealed class PopperEnvironment
{
    public PopperEnvironment(Rect viewport) => Viewport = viewport;

    /// <summary>根边界（Floating UI 的 <c>rootBoundary: 'viewport'</c>）。</summary>
    public Rect Viewport { get; }

    /// <summary>浮层的裁剪祖先（滚动容器、<c>overflow: hidden</c>）求交后的矩形；null 表示只受视口约束（浮层经 Portal 挂到覆盖层时通常如此）。</summary>
    public Rect? FloatingClippingRect { get; init; }

    /// <summary>锚点的裁剪祖先求交后的矩形；null 表示只受视口约束。<c>hide</c> 的 <c>referenceHidden</c> 与 <c>altBoundary</c> 用它。</summary>
    public Rect? ReferenceClippingRect { get; init; }

    /// <summary>浮层的书写方向是否为 RTL（影响上 / 下边时 start / end 的物理方向）。</summary>
    public bool Rtl { get; init; }
}

/// <summary><c>detectOverflow</c> 的选项（所有带溢出检测的中间件共用）。</summary>
public class DetectOverflowOptions
{
    /// <summary>与边界保持的虚拟内边距（正值 = 离边界更远就算溢出）。</summary>
    public SideValues Padding { get; init; }

    /// <summary>自定义裁剪边界（替代元素的裁剪祖先，仍与根边界求交）；null 为 <c>'clippingAncestors'</c>。</summary>
    public Rect? Boundary { get; init; }

    /// <summary>自定义根边界；null 为环境的视口。</summary>
    public Rect? RootBoundary { get; init; }

    /// <summary>检测哪个元素的溢出。</summary>
    public PopperElementContext ElementContext { get; init; }

    /// <summary>改用另一个元素的裁剪祖先（Floating UI 的 <c>altBoundary</c>）。</summary>
    public bool AltBoundary { get; init; }

    internal static readonly DetectOverflowOptions Default = new();
}
