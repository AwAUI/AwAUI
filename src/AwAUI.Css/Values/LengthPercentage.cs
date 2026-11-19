using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>
/// 计算后的 &lt;length-percentage&gt;：<c>Length px + Percentage%</c>，或一棵保留到布局时求值的 <see cref="CalcExpression"/>
/// （含百分比的 <c>min()/max()/clamp()</c> 等非线性表达式）。em/rem/视口单位在计算值阶段已换算为 px（design/16）。
/// </summary>
public readonly struct LengthPercentage : IEquatable<LengthPercentage>
{
    public LengthPercentage(float length, float percentage)
    {
        Length = length;
        Percentage = percentage;
        HasPercentage = percentage != 0;
        Calc = null;
    }

    LengthPercentage(float length, float percentage, bool hasPercentage, CalcExpression? calc)
    {
        Length = length;
        Percentage = percentage;
        HasPercentage = hasPercentage;
        Calc = calc;
    }

    public static LengthPercentage Zero => default;

    public static LengthPercentage FromPx(float px) => new(px, 0, false, null);

    /// <summary>百分比，<paramref name="percent"/> 以百分数表示（50 表示 50%）。</summary>
    public static LengthPercentage FromPercentage(float percent) => new(0, percent, true, null);

    /// <summary>线性组合 <c>calc(px + pct%)</c>。</summary>
    internal static LengthPercentage Linear(float px, float percent, bool hasPercentage) => new(px, percent, hasPercentage, null);

    /// <summary>非线性表达式（布局时求值）。</summary>
    public static LengthPercentage FromCalc(CalcExpression calc)
        => new(0, 0, true, calc ?? throw new ArgumentNullException(nameof(calc)));

    /// <summary>绝对长度部分（px）。<see cref="Calc"/> 非空时无意义。</summary>
    public float Length { get; }

    /// <summary>百分比部分（百分数）。<see cref="Calc"/> 非空时无意义。</summary>
    public float Percentage { get; }

    /// <summary>是否依赖百分比基准（布局时才能得到像素值）。</summary>
    public bool HasPercentage { get; }

    /// <summary>非线性表达式树；线性值为 null。</summary>
    public CalcExpression? Calc { get; }

    /// <summary>是否为纯 px 值（不依赖百分比基准）。</summary>
    public bool IsFixed => !HasPercentage && Calc is null;

    /// <summary>按百分比基准（px）解析为像素。基准未知（如内容尺寸下的百分比高度）时调用方应自行处理。</summary>
    public float Resolve(float percentageBasis)
    {
        if (Calc is not null) return Calc.Evaluate(percentageBasis);
        return HasPercentage ? Length + Percentage / 100f * percentageBasis : Length;
    }

    public bool Equals(LengthPercentage other)
        => Length.Equals(other.Length) && Percentage.Equals(other.Percentage) && HasPercentage == other.HasPercentage
           && (ReferenceEquals(Calc, other.Calc) || (Calc is not null && Calc.Equals(other.Calc)));

    public override bool Equals(object? obj) => obj is LengthPercentage l && Equals(l);
    public override int GetHashCode() => HashCodeCompat.Combine(Length.GetHashCode(), Percentage.GetHashCode(), Calc?.GetHashCode() ?? 0);
    public static bool operator ==(LengthPercentage a, LengthPercentage b) => a.Equals(b);
    public static bool operator !=(LengthPercentage a, LengthPercentage b) => !a.Equals(b);

    public override string ToString()
    {
        if (Calc is not null) return Calc.ToString();
        if (!HasPercentage) return ValueText.Px(Length);
        if (Length == 0) return ValueText.Percent(Percentage);
        return Length < 0
            ? "calc(" + ValueText.Percent(Percentage) + " - " + ValueText.Px(-Length) + ")"
            : "calc(" + ValueText.Percent(Percentage) + " + " + ValueText.Px(Length) + ")";
    }
}

/// <summary><see cref="SizeValue"/> 的种类。</summary>
public enum SizeKind : byte
{
    /// <summary>长度/百分比，见 <see cref="SizeValue.Value"/>。</summary>
    LengthPercentage,
    Auto,
    /// <summary><c>none</c>（<c>max-width</c> 等）。</summary>
    None,
    /// <summary><c>normal</c>（<c>gap</c>）。</summary>
    Normal,
    MinContent,
    MaxContent,
    FitContent,
    /// <summary><c>content</c>（<c>flex-basis</c>）。</summary>
    Content,
}

/// <summary>
/// 尺寸类计算值：<c>width/height/min-*/max-*</c>、<c>margin-*</c>、<c>top/right/bottom/left</c>、<c>flex-basis</c>、<c>gap</c>。
/// </summary>
public readonly struct SizeValue : IEquatable<SizeValue>
{
    public SizeValue(SizeKind kind, LengthPercentage value)
    {
        Kind = kind;
        Value = kind == SizeKind.LengthPercentage ? value : default;
    }

    public static SizeValue Auto => new(SizeKind.Auto, default);
    public static SizeValue None => new(SizeKind.None, default);
    public static SizeValue Normal => new(SizeKind.Normal, default);

    public static SizeValue FromPx(float px) => new(SizeKind.LengthPercentage, LengthPercentage.FromPx(px));

    public static SizeValue FromPercentage(float percent) => new(SizeKind.LengthPercentage, LengthPercentage.FromPercentage(percent));

    public static SizeValue From(LengthPercentage value) => new(SizeKind.LengthPercentage, value);

    public SizeKind Kind { get; }

    /// <summary><see cref="Kind"/> 为 <see cref="SizeKind.LengthPercentage"/> 时的值。</summary>
    public LengthPercentage Value { get; }

    public bool IsAuto => Kind == SizeKind.Auto;

    public bool IsLengthPercentage => Kind == SizeKind.LengthPercentage;

    public bool Equals(SizeValue other) => Kind == other.Kind && Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is SizeValue s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Value.GetHashCode());
    public static bool operator ==(SizeValue a, SizeValue b) => a.Equals(b);
    public static bool operator !=(SizeValue a, SizeValue b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        SizeKind.LengthPercentage => Value.ToString(),
        SizeKind.Auto => "auto",
        SizeKind.None => "none",
        SizeKind.Normal => "normal",
        SizeKind.MinContent => "min-content",
        SizeKind.MaxContent => "max-content",
        SizeKind.FitContent => "fit-content",
        _ => "content",
    };
}

/// <summary>圆角半径（水平、垂直）计算值。</summary>
public readonly struct CornerRadius : IEquatable<CornerRadius>
{
    public CornerRadius(LengthPercentage horizontal, LengthPercentage vertical)
    {
        Horizontal = horizontal;
        Vertical = vertical;
    }

    /// <summary>水平半径，百分比相对边框盒宽度。</summary>
    public LengthPercentage Horizontal { get; }

    /// <summary>垂直半径，百分比相对边框盒高度。</summary>
    public LengthPercentage Vertical { get; }

    public bool IsZero => Horizontal.IsFixed && Horizontal.Length == 0 && Vertical.IsFixed && Vertical.Length == 0;

    public bool Equals(CornerRadius other) => Horizontal.Equals(other.Horizontal) && Vertical.Equals(other.Vertical);
    public override bool Equals(object? obj) => obj is CornerRadius r && Equals(r);
    public override int GetHashCode() => HashCodeCompat.Combine(Horizontal.GetHashCode(), Vertical.GetHashCode());
    public static bool operator ==(CornerRadius a, CornerRadius b) => a.Equals(b);
    public static bool operator !=(CornerRadius a, CornerRadius b) => !a.Equals(b);

    public override string ToString()
        => Horizontal.Equals(Vertical) ? Horizontal.ToString() : Horizontal + " " + Vertical;
}

/// <summary>整数或 <c>auto</c>（<c>z-index</c>）。</summary>
public readonly struct IntegerOrAuto : IEquatable<IntegerOrAuto>
{
    public IntegerOrAuto(int value)
    {
        Value = value;
        IsAuto = false;
    }

    IntegerOrAuto(bool auto)
    {
        Value = 0;
        IsAuto = auto;
    }

    public static IntegerOrAuto Auto => new(true);

    public bool IsAuto { get; }

    public int Value { get; }

    public bool Equals(IntegerOrAuto other) => IsAuto == other.IsAuto && Value == other.Value;
    public override bool Equals(object? obj) => obj is IntegerOrAuto i && Equals(i);
    public override int GetHashCode() => IsAuto ? -1 : Value;
    public static bool operator ==(IntegerOrAuto a, IntegerOrAuto b) => a.Equals(b);
    public static bool operator !=(IntegerOrAuto a, IntegerOrAuto b) => !a.Equals(b);
    public override string ToString() => IsAuto ? "auto" : Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 计算后的非线性 calc 表达式树：叶子只含 px 与百分比，求值时代入百分比基准。由 <c>min()/max()/clamp()</c> 与百分比混用产生。
/// </summary>
public abstract class CalcExpression : IEquatable<CalcExpression>
{
    private protected CalcExpression() { }

    /// <summary>代入百分比基准（px）求值。结果钳制在有限范围内。</summary>
    public float Evaluate(float percentageBasis) => CalcMath.Finite(EvaluateCore(percentageBasis));

    internal abstract double EvaluateCore(double basis);

    internal abstract void Write(StringBuilder sb, bool top);

    public abstract bool Equals(CalcExpression? other);

    public override bool Equals(object? obj) => obj is CalcExpression c && Equals(c);

    public abstract override int GetHashCode();

    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb, top: true);
        return sb.ToString();
    }

    /// <summary>叶子：<c>px + pct%</c>。</summary>
    public sealed class Leaf : CalcExpression
    {
        public Leaf(double px, double percent)
        {
            Px = px;
            Percent = percent;
        }

        public double Px { get; }

        public double Percent { get; }

        internal override double EvaluateCore(double basis) => Px + Percent / 100 * basis;

        internal override void Write(StringBuilder sb, bool top)
        {
            if (Percent == 0) { sb.Append(Num(Px)).Append("px"); return; }
            if (Px == 0) { sb.Append(Num(Percent)).Append('%'); return; }
            if (top) sb.Append("calc(");
            sb.Append(Num(Percent)).Append("% ").Append(Px < 0 ? "- " : "+ ").Append(Num(Math.Abs(Px))).Append("px");
            if (top) sb.Append(')');
        }

        public override bool Equals(CalcExpression? other) => other is Leaf l && l.Px.Equals(Px) && l.Percent.Equals(Percent);
        public override int GetHashCode() => HashCodeCompat.Combine(Px.GetHashCode(), Percent.GetHashCode());
    }

    /// <summary>求和。</summary>
    public sealed class Sum : CalcExpression
    {
        public Sum(IReadOnlyList<CalcExpression> terms) => Terms = terms;

        public IReadOnlyList<CalcExpression> Terms { get; }

        internal override double EvaluateCore(double basis)
        {
            double s = 0;
            for (int i = 0; i < Terms.Count; i++) s += Terms[i].EvaluateCore(basis);
            return s;
        }

        internal override void Write(StringBuilder sb, bool top)
        {
            sb.Append(top ? "calc(" : "(");
            for (int i = 0; i < Terms.Count; i++)
            {
                if (i > 0) sb.Append(" + ");
                Terms[i].Write(sb, false);
            }
            sb.Append(')');
        }

        public override bool Equals(CalcExpression? other) => other is Sum s && ListEquals(Terms, s.Terms);
        public override int GetHashCode() => ListHash(Terms, 1);
    }

    /// <summary>乘以一个数。</summary>
    public sealed class Product : CalcExpression
    {
        public Product(CalcExpression operand, double factor)
        {
            Operand = operand;
            Factor = factor;
        }

        public CalcExpression Operand { get; }

        public double Factor { get; }

        internal override double EvaluateCore(double basis) => Operand.EvaluateCore(basis) * Factor;

        internal override void Write(StringBuilder sb, bool top)
        {
            sb.Append(top ? "calc(" : "(");
            Operand.Write(sb, false);
            sb.Append(" * ").Append(Num(Factor)).Append(')');
        }

        public override bool Equals(CalcExpression? other) => other is Product p && p.Factor.Equals(Factor) && p.Operand.Equals(Operand);
        public override int GetHashCode() => HashCodeCompat.Combine(Operand.GetHashCode(), Factor.GetHashCode());
    }

    /// <summary><c>min()</c> 或 <c>max()</c>。</summary>
    public sealed class MinMax : CalcExpression
    {
        public MinMax(bool isMin, IReadOnlyList<CalcExpression> arguments)
        {
            IsMin = isMin;
            Arguments = arguments;
        }

        public bool IsMin { get; }

        public IReadOnlyList<CalcExpression> Arguments { get; }

        internal override double EvaluateCore(double basis)
        {
            double r = Arguments[0].EvaluateCore(basis);
            for (int i = 1; i < Arguments.Count; i++)
            {
                var v = Arguments[i].EvaluateCore(basis);
                r = IsMin ? Math.Min(r, v) : Math.Max(r, v);
            }
            return r;
        }

        internal override void Write(StringBuilder sb, bool top)
        {
            sb.Append(IsMin ? "min(" : "max(");
            for (int i = 0; i < Arguments.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Arguments[i].Write(sb, true);
            }
            sb.Append(')');
        }

        public override bool Equals(CalcExpression? other) => other is MinMax m && m.IsMin == IsMin && ListEquals(Arguments, m.Arguments);
        public override int GetHashCode() => ListHash(Arguments, IsMin ? 2 : 3);
    }

    /// <summary><c>clamp(min, value, max)</c>：<c>max(min, min(value, max))</c>。</summary>
    public sealed class Clamp : CalcExpression
    {
        public Clamp(CalcExpression min, CalcExpression value, CalcExpression max)
        {
            Min = min;
            Value = value;
            Max = max;
        }

        public CalcExpression Min { get; }
        public CalcExpression Value { get; }
        public CalcExpression Max { get; }

        internal override double EvaluateCore(double basis)
            => Math.Max(Min.EvaluateCore(basis), Math.Min(Value.EvaluateCore(basis), Max.EvaluateCore(basis)));

        internal override void Write(StringBuilder sb, bool top)
        {
            sb.Append("clamp(");
            Min.Write(sb, true);
            sb.Append(", ");
            Value.Write(sb, true);
            sb.Append(", ");
            Max.Write(sb, true);
            sb.Append(')');
        }

        public override bool Equals(CalcExpression? other)
            => other is Clamp c && c.Min.Equals(Min) && c.Value.Equals(Value) && c.Max.Equals(Max);

        public override int GetHashCode() => HashCodeCompat.Combine(Min.GetHashCode(), Value.GetHashCode(), Max.GetHashCode());
    }

    static string Num(double v)
    {
        double r = Math.Round(v, 4);
        return CssSerializer.FormatNumber(r == 0 ? 0 : r);
    }

    static bool ListEquals(IReadOnlyList<CalcExpression> a, IReadOnlyList<CalcExpression> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    static int ListHash(IReadOnlyList<CalcExpression> list, int seed)
    {
        int h = seed;
        for (int i = 0; i < list.Count; i++) h = HashCodeCompat.Combine(h, list[i].GetHashCode());
        return h;
    }
}

/// <summary>数值辅助。</summary>
internal static class CalcMath
{
    /// <summary>calc 结果的有限范围（CSS Values 4：无穷钳制到实现支持的最大值，NaN 视为 0）。</summary>
    public const double MaxFinite = 1e30;

    public static float Finite(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v > MaxFinite) return (float)MaxFinite;
        if (v < -MaxFinite) return (float)-MaxFinite;
        return (float)v;
    }

    public static double FiniteDouble(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v > MaxFinite) return MaxFinite;
        if (v < -MaxFinite) return -MaxFinite;
        return v;
    }

    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
}
