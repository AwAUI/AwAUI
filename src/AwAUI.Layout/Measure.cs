using System;

namespace AwAUI.Layout;

/// <summary>可用空间的种类。</summary>
public enum AvailableSpaceKind : byte
{
    /// <summary>确定的像素值。</summary>
    Definite,
    /// <summary>按最小内容尺寸排布（文本：在每个可断行处断行）。</summary>
    MinContent,
    /// <summary>按最大内容尺寸排布（文本：只在强制换行处断行）。</summary>
    MaxContent,
}

/// <summary>某一轴上的可用空间：确定值、min-content 或 max-content。</summary>
public readonly struct AvailableSpace : IEquatable<AvailableSpace>
{
    AvailableSpace(AvailableSpaceKind kind, float value)
    {
        Kind = kind;
        Value = value;
    }

    public AvailableSpaceKind Kind { get; }

    /// <summary>确定值；非确定时为 NaN。</summary>
    public float Value { get; }

    public bool IsDefinite => Kind == AvailableSpaceKind.Definite;

    /// <summary>确定的可用空间。负值按 0 处理；NaN 与正无穷视为 max-content。</summary>
    public static AvailableSpace Definite(float value)
    {
        if (float.IsNaN(value) || float.IsPositiveInfinity(value)) return MaxContent;
        return new AvailableSpace(AvailableSpaceKind.Definite, value < 0 ? 0 : Math.Min(value, LayoutMath.MaxLength));
    }

    public static AvailableSpace MinContent => new(AvailableSpaceKind.MinContent, float.NaN);
    public static AvailableSpace MaxContent => new(AvailableSpaceKind.MaxContent, float.NaN);

    /// <summary>确定值减去 <paramref name="amount"/>（不小于 0）；非确定值原样返回。</summary>
    internal AvailableSpace Shrink(float amount) =>
        Kind == AvailableSpaceKind.Definite ? Definite(Value - amount) : this;

    public bool Equals(AvailableSpace o) => Kind == o.Kind && (Kind != AvailableSpaceKind.Definite || Value == o.Value);
    public override bool Equals(object? obj) => obj is AvailableSpace a && Equals(a);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, IsDefinite ? Value.GetHashCode() : 0);
    public static bool operator ==(AvailableSpace a, AvailableSpace b) => a.Equals(b);
    public static bool operator !=(AvailableSpace a, AvailableSpace b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        AvailableSpaceKind.MinContent => "min-content",
        AvailableSpaceKind.MaxContent => "max-content",
        _ => FormattableString.Invariant($"{Value}px"),
    };
}

/// <summary>
/// 叶子测量的约束（内容盒尺度，已扣除 padding 与 border）。
/// <para>
/// <see cref="Width"/> 为确定值时，既可能是「必须正好这么宽」（宽度已由样式或父容器确定），也可能是「最多这么宽」
/// （fit-content）；测量方应在该宽度内排布（文本在此宽度断行）并返回自然尺寸，引擎会用已确定的值覆盖相应轴。
/// <see cref="Height"/> 只在高度已由样式或父容器确定（且宽度未定）时为确定值，其余情况为 max-content：
/// 与 CSS 一致，auto 高度由内容决定，不依赖可用高度。
/// </para>
/// </summary>
public readonly struct MeasureConstraints : IEquatable<MeasureConstraints>
{
    public MeasureConstraints(AvailableSpace width, AvailableSpace height)
    {
        Width = width;
        Height = height;
    }

    public AvailableSpace Width { get; }
    public AvailableSpace Height { get; }

    public bool Equals(MeasureConstraints o) => Width.Equals(o.Width) && Height.Equals(o.Height);
    public override bool Equals(object? obj) => obj is MeasureConstraints m && Equals(m);
    public override int GetHashCode() => HashCodeCompat.Combine(Width.GetHashCode(), Height.GetHashCode());
    public override string ToString() => $"({Width}, {Height})";
}

/// <summary>
/// 叶子节点（文本、图片等）的内容测量。设置到 <see cref="LayoutNode.Measure"/> 后该节点按叶子处理。
/// <para>
/// 测量结果被引擎按约束缓存；内容变化时调用 <see cref="LayoutNode.MarkDirty"/>。
/// 测量过程中不得修改布局树或再次调用 <see cref="LayoutEngine"/>。
/// </para>
/// </summary>
public abstract class LayoutMeasure
{
    /// <summary>在给定约束下测量内容盒尺寸（不含 padding 与 border）。</summary>
    public abstract Size Measure(LayoutNode node, in MeasureConstraints constraints);

    /// <summary>
    /// 第一条基线到内容盒顶部的距离；没有基线时返回 NaN（引擎会以边框盒底边合成）。
    /// <paramref name="contentSize"/> 是最终内容盒尺寸。
    /// </summary>
    public virtual float GetBaseline(LayoutNode node, Size contentSize) => float.NaN;
}
