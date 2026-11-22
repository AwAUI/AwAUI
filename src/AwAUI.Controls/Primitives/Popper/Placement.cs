using System;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 浮层相对锚点所在的边（Radix 的 <c>side</c>，CSS 中为 <c>data-side</c>）。
/// <see cref="InlineStart"/> / <see cref="InlineEnd"/> 是逻辑方向，在定位时按 RTL 解析成左 / 右；
/// 解析后的方位（含 <c>data-side</c>）始终是物理的 top / right / bottom / left。
/// </summary>
public enum Side : byte
{
    Top,
    Right,
    Bottom,
    Left,
    InlineStart,
    InlineEnd,
}

/// <summary>
/// 浮层沿锚点边的对齐（Radix 的 <c>align</c>，CSS 中为 <c>data-align</c>）。<see cref="Start"/> / <see cref="End"/> 是逻辑方向：
/// 位于上 / 下边时，RTL 下 <see cref="Start"/> 对齐锚点的右缘（与 Floating UI 相同）。
/// </summary>
public enum PopperAlign : byte
{
    Center,
    Start,
    End,
}

/// <summary>
/// 方位 = 边 + 对齐（Floating UI 的 <c>placement</c>，如 <c>bottom-start</c>）。<see cref="PopperAlign.Center"/> 对应没有对齐后缀的基本方位。
/// </summary>
public readonly struct Placement : IEquatable<Placement>
{
    public Placement(Side side, PopperAlign align = PopperAlign.Center)
    {
        Side = side;
        Align = align;
    }

    public Side Side { get; }

    public PopperAlign Align { get; }

    /// <summary>是否为不带对齐的基本方位（<c>top</c>、<c>right</c> ...）。</summary>
    public bool IsBase => Align == PopperAlign.Center;

    public static Placement Top => new(Side.Top);
    public static Placement TopStart => new(Side.Top, PopperAlign.Start);
    public static Placement TopEnd => new(Side.Top, PopperAlign.End);
    public static Placement Right => new(Side.Right);
    public static Placement RightStart => new(Side.Right, PopperAlign.Start);
    public static Placement RightEnd => new(Side.Right, PopperAlign.End);
    public static Placement Bottom => new(Side.Bottom);
    public static Placement BottomStart => new(Side.Bottom, PopperAlign.Start);
    public static Placement BottomEnd => new(Side.Bottom, PopperAlign.End);
    public static Placement Left => new(Side.Left);
    public static Placement LeftStart => new(Side.Left, PopperAlign.Start);
    public static Placement LeftEnd => new(Side.Left, PopperAlign.End);

    static readonly Placement[] s_all =
    {
        Top, TopStart, TopEnd, Right, RightStart, RightEnd, Bottom, BottomStart, BottomEnd, Left, LeftStart, LeftEnd,
    };

    /// <summary>全部 12 个方位，顺序与 Floating UI 的 <c>placements</c> 常量一致（autoPlacement 的默认候选顺序依赖它）。</summary>
    public static IReadOnlyList<Placement> All => s_all;

    /// <summary>对边的方位，对齐不变（<c>top-start</c> → <c>bottom-start</c>）。</summary>
    public Placement Opposite => new(OppositeSide(Side), Align);

    /// <summary>对齐取反（<c>start</c> ↔ <c>end</c>），边不变。</summary>
    public Placement OppositeAlignment => new(Side, Align switch
    {
        PopperAlign.Start => PopperAlign.End,
        PopperAlign.End => PopperAlign.Start,
        _ => PopperAlign.Center,
    });

    /// <summary>边是否在竖直方向（上 / 下）：主轴为 y。</summary>
    public bool IsVertical => Side is Side.Top or Side.Bottom;

    /// <summary>把逻辑行内边解析成物理边：LTR 的 <c>inline-start</c> 在左，RTL 在右；物理边原样返回。</summary>
    public static Side ResolveSide(Side side, bool rtl) => side switch
    {
        Side.InlineStart => rtl ? Side.Right : Side.Left,
        Side.InlineEnd => rtl ? Side.Left : Side.Right,
        _ => side,
    };

    public static Side OppositeSide(Side side) => side switch
    {
        Side.Top => Side.Bottom,
        Side.Bottom => Side.Top,
        Side.Left => Side.Right,
        Side.Right => Side.Left,
        Side.InlineStart => Side.InlineEnd,
        _ => Side.InlineStart,
    };

    /// <summary>CSS 属性值形式：<c>data-side</c> 的取值（<c>top / right / bottom / left</c>，逻辑边为 <c>inline-start / inline-end</c>）。</summary>
    public static string SideName(Side side) => side switch
    {
        Side.Top => "top",
        Side.Right => "right",
        Side.Bottom => "bottom",
        Side.Left => "left",
        Side.InlineStart => "inline-start",
        _ => "inline-end",
    };

    /// <summary>CSS 属性值形式：<c>data-align</c> 的取值（<c>start / center / end</c>）。</summary>
    public static string AlignName(PopperAlign align) => align switch
    {
        PopperAlign.Start => "start",
        PopperAlign.End => "end",
        _ => "center",
    };

    /// <summary>解析 <c>bottom-start</c> 这类 Floating UI 方位名（区分大小写，与 CSS 关键字一致）。</summary>
    public static bool TryParse(string? text, out Placement placement)
    {
        placement = default;
        if (text is null) return false;
        Side side;
        int dash;
        if (text == "inline-start") { side = Side.InlineStart; dash = -1; }
        else if (text == "inline-end") { side = Side.InlineEnd; dash = -1; }
        else
        {
            dash = text.IndexOf('-');
            string sidePart = dash < 0 ? text : text.Substring(0, dash);
            switch (sidePart)
            {
                case "top": side = Side.Top; break;
                case "right": side = Side.Right; break;
                case "bottom": side = Side.Bottom; break;
                case "left": side = Side.Left; break;
                default: return false;
            }
        }
        var align = PopperAlign.Center;
        if (dash >= 0)
        {
            switch (text.Substring(dash + 1))
            {
                case "start": align = PopperAlign.Start; break;
                case "end": align = PopperAlign.End; break;
                default: return false;
            }
        }
        placement = new Placement(side, align);
        return true;
    }

    public static Placement Parse(string text) =>
        TryParse(text, out var p) ? p : throw new FormatException("无效的方位：" + text);

    public bool Equals(Placement other) => Side == other.Side && Align == other.Align;

    public override bool Equals(object? obj) => obj is Placement p && Equals(p);

    public override int GetHashCode() => (int)Side * 3 + (int)Align;

    public static bool operator ==(Placement a, Placement b) => a.Equals(b);

    public static bool operator !=(Placement a, Placement b) => !a.Equals(b);

    public override string ToString() => IsBase ? SideName(Side) : SideName(Side) + "-" + AlignName(Align);

    // ------------------------------------------------------------ 轴（内部）

    /// <summary>边所在的轴：上 / 下为 y，左 / 右为 x（Floating UI 的 <c>getSideAxis</c>）。</summary>
    internal Axis SideAxis => IsVertical ? Axis.Y : Axis.X;

    /// <summary>对齐所在的轴（<c>getAlignmentAxis</c>）。</summary>
    internal Axis AlignmentAxis => IsVertical ? Axis.X : Axis.Y;
}

/// <summary>坐标轴。</summary>
internal enum Axis : byte
{
    X,
    Y,
}
