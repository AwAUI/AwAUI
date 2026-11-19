using System;
using System.Collections.Generic;
using System.Numerics;

namespace AwAUI.Graphics;

/// <summary>画刷基类。全部不可变。</summary>
public abstract class Brush
{
    private protected Brush() { }
}

public sealed class SolidColorBrush : Brush
{
    public SolidColorBrush(Color color) => Color = color;
    public Color Color { get; }
}

public readonly struct GradientStop
{
    public GradientStop(float offset, Color color) { Offset = offset; Color = color; }
    public float Offset { get; }
    public Color Color { get; }
}

/// <summary>渐变在 [0,1] 之外的扩展方式。</summary>
public enum SpreadMode : byte { Pad, Repeat, Reflect }

public abstract class GradientBrush : Brush
{
    private protected GradientBrush(IReadOnlyList<GradientStop> stops, SpreadMode spread, Matrix3x2 transform)
    {
        if (stops.Count == 0) throw new ArgumentException("Gradient requires at least one stop.", nameof(stops));
        // 按 CSS 规则修正色标：偏移单调不减。NaN 偏移视为与前一个色标相同（第一个为 0）：
        // Math.Max 会传播 NaN，不处理的话一个 NaN 会让其后全部色标变成 NaN
        var s = new GradientStop[stops.Count];
        float last = float.MinValue;
        for (int i = 0; i < s.Length; i++)
        {
            float o = stops[i].Offset;
            if (float.IsNaN(o)) o = i == 0 ? 0 : last;
            o = Math.Max(o, last);
            s[i] = new GradientStop(o, stops[i].Color);
            last = o;
        }
        Stops = s;
        Spread = spread;
        Transform = transform;
    }

    public IReadOnlyList<GradientStop> Stops { get; }
    public SpreadMode Spread { get; }

    /// <summary>渐变空间到绘制局部空间的附加变换。</summary>
    public Matrix3x2 Transform { get; }
}

public sealed class LinearGradientBrush : GradientBrush
{
    public LinearGradientBrush(Point start, Point end, IReadOnlyList<GradientStop> stops,
        SpreadMode spread = SpreadMode.Pad, Matrix3x2? transform = null)
        : base(stops, spread, transform ?? Matrix3x2.Identity)
    {
        Start = start;
        End = end;
    }

    public Point Start { get; }
    public Point End { get; }
}

/// <summary>
/// 径向渐变。简单形式：t = 到 <see cref="Center"/> 的距离 / <see cref="Radius"/>。
/// <para>
/// 带焦点的形式（双点锥形渐变，与 Canvas 2D 的 <c>createRadialGradient(fx, fy, fr, cx, cy, r)</c>、SVG 2 的 fx/fy/fr 相同）：
/// 起始圆（<see cref="FocalPoint"/>，<see cref="FocalRadius"/>）对应 t = 0，结束圆（<see cref="Center"/>，<see cref="Radius"/>）对应 t = 1，
/// 中间的圆心与半径随 t 线性插值。像素取「经过它且半径 ≥ 0 的圆」中最大的 t，再按扩展模式映射；没有这样的圆的像素不绘制（透明）。
/// 焦点在结束圆之外时结果是一个锥形（SVG 1.1 会把焦点移到圆上，那是标记语言一侧的规则）。两个圆完全相同时什么都不画。
/// </para>
/// </summary>
public sealed class RadialGradientBrush : GradientBrush
{
    public RadialGradientBrush(Point center, float radius, IReadOnlyList<GradientStop> stops,
        SpreadMode spread = SpreadMode.Pad, Matrix3x2? transform = null)
        : this(center, radius, center, 0, stops, spread, transform)
    {
    }

    /// <param name="focalPoint">起始圆（t = 0）的圆心。</param>
    /// <param name="focalRadius">起始圆的半径；负值与 NaN 按 0 处理。</param>
    public RadialGradientBrush(Point center, float radius, Point focalPoint, float focalRadius, IReadOnlyList<GradientStop> stops,
        SpreadMode spread = SpreadMode.Pad, Matrix3x2? transform = null)
        : base(stops, spread, transform ?? Matrix3x2.Identity)
    {
        Center = center;
        Radius = radius;
        FocalPoint = focalPoint;
        FocalRadius = focalRadius > 0 ? focalRadius : 0;
    }

    /// <summary>结束圆（t = 1）的圆心。</summary>
    public Point Center { get; }

    /// <summary>结束圆的半径。</summary>
    public float Radius { get; }

    /// <summary>起始圆（t = 0）的圆心；简单径向渐变时等于 <see cref="Center"/>。</summary>
    public Point FocalPoint { get; }

    /// <summary>起始圆的半径；简单径向渐变时为 0。</summary>
    public float FocalRadius { get; }

    /// <summary>是否为带焦点的双点锥形渐变（焦点不在圆心，或起始圆半径不为 0）。</summary>
    public bool HasFocus =>
        !(FocalPoint.X.Equals(Center.X) && FocalPoint.Y.Equals(Center.Y)) || FocalRadius != 0;   // Equals：NaN 圆心的简单形式仍是简单形式
}

/// <summary>锥形渐变：t = 自起始角顺时针转过的角度 / 360°。</summary>
public sealed class ConicGradientBrush : GradientBrush
{
    public ConicGradientBrush(Point center, float startAngle, IReadOnlyList<GradientStop> stops,
        Matrix3x2? transform = null)
        : base(stops, SpreadMode.Pad, transform ?? Matrix3x2.Identity)
    {
        Center = center;
        StartAngle = startAngle;
    }

    public Point Center { get; }

    /// <summary>起始角（弧度），0 指向 +x 方向。</summary>
    public float StartAngle { get; }
}
