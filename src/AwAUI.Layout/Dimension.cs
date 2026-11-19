using System;
using System.Globalization;

namespace AwAUI.Layout;

/// <summary>尺寸值的种类。不同属性只接受其中的一个子集。</summary>
public enum DimensionKind : byte
{
    /// <summary><c>auto</c>；用于 <c>max-*</c> 时表示 <c>none</c>；用于 <c>min-*</c> 时表示自动最小尺寸。</summary>
    Auto,
    /// <summary>逻辑像素长度。</summary>
    Length,
    /// <summary>百分比（<see cref="Dimension.Value"/> 为 0–100 的百分数）。</summary>
    Percent,
    /// <summary><c>calc()</c> 等需要在布局时按百分比基准求值的表达式，见 <see cref="LayoutCalc"/>。</summary>
    Calc,
    /// <summary><c>min-content</c>（只在水平方向有意义；高度上按 auto 处理）。</summary>
    MinContent,
    /// <summary><c>max-content</c>（只在水平方向有意义；高度上按 auto 处理）。</summary>
    MaxContent,
    /// <summary><c>fit-content</c>：min(max-content, max(min-content, 可用空间))（只在水平方向有意义）。</summary>
    FitContent,
    /// <summary><c>content</c>，只对 <c>flex-basis</c> 有意义：按内容决定基准尺寸。</summary>
    Content,
}

/// <summary>
/// 布局时才能求值的长度表达式（CSS <c>calc()</c>、<c>min()</c>、<c>max()</c>、<c>clamp()</c> 等）。
/// 由 CSS 计算值层实现；纯 px 与纯百分比不经过它，不产生分配。
/// <para>
/// <see cref="LayoutStyle"/> 的相等比较对表达式调用 <see cref="object.Equals(object)"/>：实现方应按表达式内容重写
/// <c>Equals</c> / <c>GetHashCode</c>（或复用同一实例），否则每次样式重算产生的新实例都会被视为样式变化而触发重排。
/// <see cref="Resolve"/> 必须是纯函数（同一基准总是返回同一结果），布局缓存依赖这一点。
/// </para>
/// </summary>
public abstract class LayoutCalc
{
    /// <summary>以给定的百分比基准（逻辑像素）求值。基准不确定时传入 <see cref="float.NaN"/>。</summary>
    public abstract float Resolve(float percentBasis);

    /// <summary>表达式是否引用百分比。为 false 时即使百分比基准不确定也能求值。</summary>
    public virtual bool HasPercentage => true;
}

/// <summary>
/// 尺寸属性的值：<c>width/height/min-*/max-*/flex-basis</c>。
/// 可从 <see cref="float"/>（px）隐式转换。默认值是 <see cref="Auto"/>。
/// </summary>
public readonly struct Dimension : IEquatable<Dimension>
{
    readonly LayoutCalc? _calc;

    Dimension(DimensionKind kind, float value, LayoutCalc? calc)
    {
        Kind = kind;
        Value = value;
        _calc = calc;
    }

    /// <summary>值的种类。</summary>
    public DimensionKind Kind { get; }

    /// <summary>长度（px）或百分数（0–100）；其他种类为 0。</summary>
    public float Value { get; }

    /// <summary><see cref="DimensionKind.Calc"/> 的表达式。</summary>
    public LayoutCalc? Calc => _calc;

    public static Dimension Auto => default;
    /// <summary><c>max-*</c> 的 <c>none</c>，与 <see cref="Auto"/> 相同。</summary>
    public static Dimension None => default;
    public static Dimension MinContent => new(DimensionKind.MinContent, 0, null);
    public static Dimension MaxContent => new(DimensionKind.MaxContent, 0, null);
    public static Dimension FitContent => new(DimensionKind.FitContent, 0, null);
    public static Dimension Content => new(DimensionKind.Content, 0, null);
    public static Dimension Px(float px) => new(DimensionKind.Length, px, null);
    /// <summary>百分比；<paramref name="percent"/> 是百分数，例如 50 表示 50%。</summary>
    public static Dimension Percent(float percent) => new(DimensionKind.Percent, percent, null);
    public static Dimension Of(LayoutCalc calc) =>
        new(DimensionKind.Calc, 0, calc ?? throw new ArgumentNullException(nameof(calc)));

    public bool IsAuto => Kind == DimensionKind.Auto;

    public static implicit operator Dimension(float px) => Px(px);

    /// <summary>把 px / % / calc 解析为像素；基准不确定的百分比与其他关键字返回 NaN。</summary>
    internal float Resolve(float basis) => LengthMath.Resolve(Kind, Value, _calc, basis);

    public bool Equals(Dimension other) => Kind == other.Kind && Value.Equals(other.Value) && Equals(_calc, other._calc);
    public override bool Equals(object? obj) => obj is Dimension d && Equals(d);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Value.GetHashCode(), _calc?.GetHashCode() ?? 0);
    public static bool operator ==(Dimension a, Dimension b) => a.Equals(b);
    public static bool operator !=(Dimension a, Dimension b) => !a.Equals(b);
    public override string ToString() => LengthMath.Format(Kind, Value);
}

/// <summary><c>&lt;length-percentage&gt;</c>：padding、gap 等。可从 <see cref="float"/>（px）隐式转换。默认值为 0px。</summary>
public readonly struct LengthPercentage : IEquatable<LengthPercentage>
{
    readonly LayoutCalc? _calc;

    // default 必须是 0px，所以 DimensionKind.Auto（=0）在这里被解释为 Length
    readonly DimensionKind _kind;

    LengthPercentage(DimensionKind kind, float value, LayoutCalc? calc)
    {
        _kind = kind;
        Value = value;
        _calc = calc;
    }

    /// <summary><see cref="DimensionKind.Length"/>、<see cref="DimensionKind.Percent"/> 或 <see cref="DimensionKind.Calc"/>。</summary>
    public DimensionKind Kind => _kind == DimensionKind.Auto ? DimensionKind.Length : _kind;

    public float Value { get; }
    public LayoutCalc? Calc => _calc;

    public static LengthPercentage Zero => default;
    public static LengthPercentage Px(float px) => new(DimensionKind.Length, px, null);
    public static LengthPercentage Percent(float percent) => new(DimensionKind.Percent, percent, null);
    public static LengthPercentage Of(LayoutCalc calc) =>
        new(DimensionKind.Calc, 0, calc ?? throw new ArgumentNullException(nameof(calc)));

    public static implicit operator LengthPercentage(float px) => Px(px);
    public static implicit operator LengthPercentageAuto(LengthPercentage v) => LengthPercentageAuto.From(v.Kind, v.Value, v._calc);
    public static implicit operator Dimension(LengthPercentage v) =>
        v.Kind switch
        {
            DimensionKind.Percent => Dimension.Percent(v.Value),
            DimensionKind.Calc => Dimension.Of(v._calc!),
            _ => Dimension.Px(v.Value),
        };

    /// <summary>解析为像素；基准不确定的百分比与非有限值返回 NaN。</summary>
    internal float ResolveOrNaN(float basis)
    {
        float v = LengthMath.Resolve(Kind, Value, _calc, basis);
        return float.IsInfinity(v) ? float.NaN : v;
    }

    /// <summary>解析为像素。基准不确定（NaN）时百分比按 0 处理；非有限值按 0 处理。</summary>
    internal float ResolveOrZero(float basis)
    {
        float v = LengthMath.Resolve(Kind, Value, _calc, basis);
        return float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;
    }

    public bool Equals(LengthPercentage other) => Kind == other.Kind && Value.Equals(other.Value) && Equals(_calc, other._calc);
    public override bool Equals(object? obj) => obj is LengthPercentage d && Equals(d);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Value.GetHashCode(), _calc?.GetHashCode() ?? 0);
    public static bool operator ==(LengthPercentage a, LengthPercentage b) => a.Equals(b);
    public static bool operator !=(LengthPercentage a, LengthPercentage b) => !a.Equals(b);
    public override string ToString() => LengthMath.Format(Kind, Value);
}

/// <summary><c>&lt;length-percentage&gt; | auto</c>：margin 与 inset。可从 <see cref="float"/>（px）隐式转换。默认值为 0px。</summary>
public readonly struct LengthPercentageAuto : IEquatable<LengthPercentageAuto>
{
    // 默认值必须是 0px（margin 的初始值），所以内部用「是否为 auto」的标志而不是让 Auto 成为 default
    readonly LayoutCalc? _calc;
    readonly DimensionKind _kind; // 0 (Auto) 在这里被解释为 Length；真正的 auto 用 _isAuto 表示
    readonly bool _isAuto;

    LengthPercentageAuto(DimensionKind kind, float value, LayoutCalc? calc, bool isAuto)
    {
        _kind = kind;
        Value = value;
        _calc = calc;
        _isAuto = isAuto;
    }

    internal static LengthPercentageAuto From(DimensionKind kind, float value, LayoutCalc? calc) =>
        new(kind == DimensionKind.Auto ? DimensionKind.Length : kind, value, calc, false);

    /// <summary><see cref="DimensionKind.Auto"/>、<see cref="DimensionKind.Length"/>、<see cref="DimensionKind.Percent"/> 或 <see cref="DimensionKind.Calc"/>。</summary>
    public DimensionKind Kind => _isAuto ? DimensionKind.Auto : _kind == DimensionKind.Auto ? DimensionKind.Length : _kind;
    public float Value { get; }
    public LayoutCalc? Calc => _calc;
    public bool IsAuto => _isAuto;

    public static LengthPercentageAuto Zero => default;
    public static LengthPercentageAuto Auto => new(DimensionKind.Auto, 0, null, true);
    public static LengthPercentageAuto Px(float px) => new(DimensionKind.Length, px, null, false);
    public static LengthPercentageAuto Percent(float percent) => new(DimensionKind.Percent, percent, null, false);
    public static LengthPercentageAuto Of(LayoutCalc calc) =>
        new(DimensionKind.Calc, 0, calc ?? throw new ArgumentNullException(nameof(calc)), false);

    public static implicit operator LengthPercentageAuto(float px) => Px(px);

    /// <summary>解析为像素；auto、基准不确定的百分比与非有限值返回 NaN（按 auto 处理）。</summary>
    internal float Resolve(float basis)
    {
        if (_isAuto) return float.NaN;
        float v = LengthMath.Resolve(Kind, Value, _calc, basis);
        return float.IsInfinity(v) ? float.NaN : v;
    }

    public bool Equals(LengthPercentageAuto other) =>
        Kind == other.Kind && Value.Equals(other.Value) && Equals(_calc, other._calc);
    public override bool Equals(object? obj) => obj is LengthPercentageAuto d && Equals(d);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Value.GetHashCode(), _calc?.GetHashCode() ?? 0);
    public static bool operator ==(LengthPercentageAuto a, LengthPercentageAuto b) => a.Equals(b);
    public static bool operator !=(LengthPercentageAuto a, LengthPercentageAuto b) => !a.Equals(b);
    public override string ToString() => LengthMath.Format(Kind, Value);
}

static class LengthMath
{
    public static float Resolve(DimensionKind kind, float value, LayoutCalc? calc, float basis)
    {
        switch (kind)
        {
            case DimensionKind.Length:
                return LayoutMath.Lim(value);
            case DimensionKind.Percent:
                return float.IsNaN(basis) ? float.NaN : LayoutMath.Lim(value * 0.01f * basis);
            case DimensionKind.Calc:
                if (float.IsNaN(basis) && calc!.HasPercentage) return float.NaN;
                return LayoutMath.Lim(calc!.Resolve(basis));
            default:
                return float.NaN;
        }
    }

    public static string Format(DimensionKind kind, float value) => kind switch
    {
        DimensionKind.Length => value.ToString(CultureInfo.InvariantCulture) + "px",
        DimensionKind.Percent => value.ToString(CultureInfo.InvariantCulture) + "%",
        DimensionKind.Calc => "calc(...)",
        DimensionKind.MinContent => "min-content",
        DimensionKind.MaxContent => "max-content",
        DimensionKind.FitContent => "fit-content",
        DimensionKind.Content => "content",
        _ => "auto",
    };
}
