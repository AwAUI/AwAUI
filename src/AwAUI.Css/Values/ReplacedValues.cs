using System;

namespace AwAUI.Css.Values;

/// <summary>
/// CSS <c>&lt;position&gt;</c> 的计算值（<c>object-position</c>）：两个轴各一个长度百分比，百分比与 <c>calc()</c> 保留到使用时解析。
/// 关键字已换算：<c>left / top</c> = 0%，<c>center</c> = 50%，<c>right / bottom</c> = 100%，<c>right 10px</c> = <c>calc(100% - 10px)</c>。
/// <para>
/// 对 <c>object-position</c>，百分比相对「内容盒尺寸 − 对象尺寸」（可为负），见 <see cref="ResolveX"/>。
/// </para>
/// </summary>
public readonly struct PositionValue : IEquatable<PositionValue>
{
    public PositionValue(LengthPercentage x, LengthPercentage y)
    {
        X = x;
        Y = y;
    }

    /// <summary>水平分量。</summary>
    public LengthPercentage X { get; }

    /// <summary>垂直分量。</summary>
    public LengthPercentage Y { get; }

    /// <summary><c>50% 50%</c>（<c>object-position</c> 的初始值）。</summary>
    public static PositionValue Center => new(LengthPercentage.FromPercentage(50), LengthPercentage.FromPercentage(50));

    /// <summary>按「剩余空间」<paramref name="freeWidth"/>（容器宽 − 对象宽，可为负）解析水平偏移。</summary>
    public float ResolveX(float freeWidth) => X.Resolve(freeWidth);

    /// <summary>按剩余空间解析垂直偏移。</summary>
    public float ResolveY(float freeHeight) => Y.Resolve(freeHeight);

    public bool Equals(PositionValue other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is PositionValue p && Equals(p);

    public override int GetHashCode() => HashCodeCompat.Combine(X.GetHashCode(), Y.GetHashCode());

    public static bool operator ==(PositionValue a, PositionValue b) => a.Equals(b);

    public static bool operator !=(PositionValue a, PositionValue b) => !a.Equals(b);

    /// <summary>计算值的 CSS 文本（两个分量，如 <c>50% 50%</c>、<c>calc(100% - 10px) 0px</c>）。</summary>
    public override string ToString() => X.ToString() + " " + Y.ToString();
}

/// <summary>
/// <c>aspect-ratio</c> 的计算值：<c>auto || &lt;ratio&gt;</c>。
/// <list type="bullet">
/// <item><c>auto</c>：替换元素用其固有宽高比，其他元素没有首选宽高比。</item>
/// <item><c>&lt;ratio&gt;</c>：首选宽高比为该比值（替换元素也忽略固有宽高比）。</item>
/// <item><c>auto &lt;ratio&gt;</c>：替换元素有固有宽高比时用固有的，否则用该比值。</item>
/// </list>
/// 比值的任一项为 0 时是退化比值，按 <c>auto</c> 处理（<see cref="Ratio"/> 为 null），但计算值仍保留两个数。
/// </summary>
public readonly struct AspectRatioValue : IEquatable<AspectRatioValue>
{
    readonly bool _hasRatio;
    readonly bool _autoWithRatio;

    /// <param name="auto">是否带 <c>auto</c> 关键字。</param>
    /// <param name="width">比值的前项（非负，非有限值按 0）。</param>
    /// <param name="height">比值的后项（非负，非有限值按 0）。</param>
    public AspectRatioValue(bool auto, float width, float height)
    {
        _hasRatio = true;
        _autoWithRatio = auto;
        Width = Clean(width);
        Height = Clean(height);
    }

    /// <summary>只有 <c>auto</c>（初始值）。<c>default</c> 与它相等。</summary>
    public static AspectRatioValue Auto => default;

    /// <summary>是否带 <c>auto</c>（单独的 <c>auto</c>，或 <c>auto &lt;ratio&gt;</c>）。</summary>
    public bool IsAuto => !_hasRatio || _autoWithRatio;

    /// <summary>是否给出了 <c>&lt;ratio&gt;</c>。</summary>
    public bool HasRatio => _hasRatio;

    /// <summary>比值前项；没有比值时为 0。</summary>
    public float Width { get; }

    /// <summary>比值后项；没有比值时为 0。</summary>
    public float Height { get; }

    /// <summary>宽 / 高；没有比值或比值退化（任一项为 0、结果非有限）时为 null。</summary>
    public float? Ratio
    {
        get
        {
            if (!_hasRatio || !(Width > 0) || !(Height > 0)) return null;
            float r = Width / Height;
            return r > 0 && !float.IsInfinity(r) ? r : null;
        }
    }

    /// <summary>
    /// 使用的宽高比：<paramref name="naturalRatio"/> 是替换元素的固有宽高比（非替换元素或没有时传 null）。
    /// 没有可用比值时返回 null。
    /// </summary>
    /// <remarks>退化比值按 <c>auto</c> 处理（CSS Sizing 4），即返回 <paramref name="naturalRatio"/>。</remarks>
    public float? Resolve(float? naturalRatio)
    {
        if (_autoWithRatio && naturalRatio is not null) return naturalRatio;
        return Ratio ?? naturalRatio;
    }

    static float Clean(float v) => float.IsNaN(v) || v < 0 ? 0 : float.IsInfinity(v) ? float.MaxValue : v;

    public bool Equals(AspectRatioValue other) =>
        _hasRatio == other._hasRatio && _autoWithRatio == other._autoWithRatio && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is AspectRatioValue a && Equals(a);

    public override int GetHashCode() =>
        HashCodeCompat.Combine((_hasRatio ? 1 : 0) | (_autoWithRatio ? 2 : 0), Width.GetHashCode(), Height.GetHashCode());

    public static bool operator ==(AspectRatioValue a, AspectRatioValue b) => a.Equals(b);

    public static bool operator !=(AspectRatioValue a, AspectRatioValue b) => !a.Equals(b);

    /// <summary>计算值的 CSS 文本：<c>auto</c>、<c>16 / 9</c>、<c>auto 2 / 1</c>（单个数按 <c>/ 1</c> 补全，与浏览器一致）。</summary>
    public override string ToString()
    {
        if (!_hasRatio) return "auto";
        var ratio = ValueText.Number(Width) + " / " + ValueText.Number(Height);
        return _autoWithRatio ? "auto " + ratio : ratio;
    }
}

/// <summary><c>&lt;position&gt;</c> 的指定值（两个轴的长度百分比表达式）。</summary>
internal sealed class PositionSpecifiedValue : CssValue
{
    public PositionSpecifiedValue(CalcNode x, CalcNode y)
    {
        X = x;
        Y = y;
    }

    public CalcNode X { get; }

    public CalcNode Y { get; }
}

/// <summary><c>aspect-ratio</c> 的指定值。</summary>
internal sealed class AspectRatioSpecifiedValue : CssValue
{
    public static readonly AspectRatioSpecifiedValue Auto = new(true, null, null);

    public AspectRatioSpecifiedValue(bool auto, CalcNode? width, CalcNode? height)
    {
        IsAuto = auto;
        Width = width;
        Height = height;
    }

    public bool IsAuto { get; }

    /// <summary>比值前项；只有 <c>auto</c> 时为 null。</summary>
    public CalcNode? Width { get; }

    /// <summary>比值后项；省略（单个数）时为 null，按 1。</summary>
    public CalcNode? Height { get; }
}
