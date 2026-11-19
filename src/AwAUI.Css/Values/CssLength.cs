using System;
using System.Globalization;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>长度单位（CSS Values 4）。百分比不是长度单位，以 <see cref="Percent"/> 表示在同一套结构中携带。</summary>
public enum CssLengthUnit : byte
{
    Px,
    Cm,
    Mm,
    Q,
    In,
    Pt,
    Pc,
    /// <summary>相对元素自身字体大小（用于 <c>font-size</c> 时相对父元素）。</summary>
    Em,
    /// <summary>相对根元素字体大小。</summary>
    Rem,
    /// <summary>x-height，没有字体度量时按 0.5em 近似。</summary>
    Ex,
    /// <summary>「0」的步进宽度，没有字体度量时按 0.5em 近似。</summary>
    Ch,
    Vw,
    Vh,
    Vmin,
    Vmax,
    /// <summary>行内方向视口单位（横排即 vw）。</summary>
    Vi,
    /// <summary>块方向视口单位（横排即 vh）。</summary>
    Vb,
    Svw,
    Svh,
    Svmin,
    Svmax,
    Lvw,
    Lvh,
    Lvmin,
    Lvmax,
    Dvw,
    Dvh,
    Dvmin,
    Dvmax,
    /// <summary>百分比（相对某个百分比基准，布局时解析）。</summary>
    Percent,
}

/// <summary>
/// 解析相对长度所需的上下文：字体大小与视口尺寸。计算值阶段由级联提供，布局阶段可自行构造。
/// </summary>
public readonly struct LengthContext
{
    public LengthContext(double fontSize, double rootFontSize, CssEnvironment environment)
    {
        FontSize = fontSize;
        RootFontSize = rootFontSize;
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary><c>em</c> 的基准（px）。</summary>
    public double FontSize { get; }

    /// <summary><c>rem</c> 的基准（px）。</summary>
    public double RootFontSize { get; }

    /// <summary>视口、安全区等环境值。</summary>
    public CssEnvironment Environment { get; }
}

/// <summary>带单位的长度（指定值）。百分比以 <see cref="CssLengthUnit.Percent"/> 表示。</summary>
public readonly struct CssLength : IEquatable<CssLength>
{
    public CssLength(double value, CssLengthUnit unit)
    {
        Value = value;
        Unit = unit;
    }

    public double Value { get; }

    public CssLengthUnit Unit { get; }

    public static CssLength Px(double value) => new(value, CssLengthUnit.Px);

    public bool IsPercentage => Unit == CssLengthUnit.Percent;

    /// <summary>是否为绝对长度（px、cm、in 等），无需上下文即可换算。</summary>
    public bool IsAbsolute => Unit <= CssLengthUnit.Pc;

    /// <summary>是否依赖字体大小。</summary>
    public bool IsFontRelative => Unit is CssLengthUnit.Em or CssLengthUnit.Rem or CssLengthUnit.Ex or CssLengthUnit.Ch;

    /// <summary>是否依赖视口尺寸。</summary>
    public bool IsViewportRelative => Unit >= CssLengthUnit.Vw && Unit <= CssLengthUnit.Dvmax;

    /// <summary>把非百分比长度换算为 px。百分比不能在此换算，抛出异常。</summary>
    public double ToPx(in LengthContext context)
    {
        var env = context.Environment;
        switch (Unit)
        {
            case CssLengthUnit.Px: return Value;
            case CssLengthUnit.Em: return Value * context.FontSize;
            case CssLengthUnit.Rem: return Value * context.RootFontSize;
            case CssLengthUnit.Ex:
            case CssLengthUnit.Ch: return Value * context.FontSize * 0.5;
            case CssLengthUnit.Vw:
            case CssLengthUnit.Vi: return Value * env.ViewportWidth / 100;
            case CssLengthUnit.Vh:
            case CssLengthUnit.Vb: return Value * env.ViewportHeight / 100;
            case CssLengthUnit.Vmin: return Value * Math.Min(env.ViewportWidth, env.ViewportHeight) / 100;
            case CssLengthUnit.Vmax: return Value * Math.Max(env.ViewportWidth, env.ViewportHeight) / 100;
            case CssLengthUnit.Svw: return Value * env.SmallViewportWidth / 100;
            case CssLengthUnit.Svh: return Value * env.SmallViewportHeight / 100;
            case CssLengthUnit.Svmin: return Value * Math.Min(env.SmallViewportWidth, env.SmallViewportHeight) / 100;
            case CssLengthUnit.Svmax: return Value * Math.Max(env.SmallViewportWidth, env.SmallViewportHeight) / 100;
            case CssLengthUnit.Lvw: return Value * env.LargeViewportWidth / 100;
            case CssLengthUnit.Lvh: return Value * env.LargeViewportHeight / 100;
            case CssLengthUnit.Lvmin: return Value * Math.Min(env.LargeViewportWidth, env.LargeViewportHeight) / 100;
            case CssLengthUnit.Lvmax: return Value * Math.Max(env.LargeViewportWidth, env.LargeViewportHeight) / 100;
            case CssLengthUnit.Dvw: return Value * env.ViewportWidth / 100;
            case CssLengthUnit.Dvh: return Value * env.ViewportHeight / 100;
            case CssLengthUnit.Dvmin: return Value * Math.Min(env.ViewportWidth, env.ViewportHeight) / 100;
            case CssLengthUnit.Dvmax: return Value * Math.Max(env.ViewportWidth, env.ViewportHeight) / 100;
            case CssLengthUnit.Percent: throw new InvalidOperationException("百分比需要百分比基准，不能直接换算为 px。");
            default: return Value * AbsoluteFactor(Unit);
        }
    }

    /// <summary>绝对单位到 px 的换算系数（1in = 96px）。</summary>
    internal static double AbsoluteFactor(CssLengthUnit unit) => unit switch
    {
        CssLengthUnit.Px => 1,
        CssLengthUnit.Cm => 96 / 2.54,
        CssLengthUnit.Mm => 96 / 25.4,
        CssLengthUnit.Q => 96 / 101.6,
        CssLengthUnit.In => 96,
        CssLengthUnit.Pt => 96.0 / 72,
        CssLengthUnit.Pc => 16,
        _ => double.NaN,
    };

    /// <summary>按 ASCII 大小写不敏感识别长度单位（不含 %）。</summary>
    public static bool TryParseUnit(string unit, out CssLengthUnit result)
    {
        result = CssLengthUnit.Px;
        if (unit.Length is < 1 or > 5) return false;
        switch (CssSerializer.ToAsciiLower(unit))
        {
            case "px": result = CssLengthUnit.Px; return true;
            case "cm": result = CssLengthUnit.Cm; return true;
            case "mm": result = CssLengthUnit.Mm; return true;
            case "q": result = CssLengthUnit.Q; return true;
            case "in": result = CssLengthUnit.In; return true;
            case "pt": result = CssLengthUnit.Pt; return true;
            case "pc": result = CssLengthUnit.Pc; return true;
            case "em": result = CssLengthUnit.Em; return true;
            case "rem": result = CssLengthUnit.Rem; return true;
            case "ex": result = CssLengthUnit.Ex; return true;
            case "ch": result = CssLengthUnit.Ch; return true;
            case "vw": result = CssLengthUnit.Vw; return true;
            case "vh": result = CssLengthUnit.Vh; return true;
            case "vmin": result = CssLengthUnit.Vmin; return true;
            case "vmax": result = CssLengthUnit.Vmax; return true;
            case "vi": result = CssLengthUnit.Vi; return true;
            case "vb": result = CssLengthUnit.Vb; return true;
            case "svw": result = CssLengthUnit.Svw; return true;
            case "svh": result = CssLengthUnit.Svh; return true;
            case "svmin": result = CssLengthUnit.Svmin; return true;
            case "svmax": result = CssLengthUnit.Svmax; return true;
            case "lvw": result = CssLengthUnit.Lvw; return true;
            case "lvh": result = CssLengthUnit.Lvh; return true;
            case "lvmin": result = CssLengthUnit.Lvmin; return true;
            case "lvmax": result = CssLengthUnit.Lvmax; return true;
            case "dvw": result = CssLengthUnit.Dvw; return true;
            case "dvh": result = CssLengthUnit.Dvh; return true;
            case "dvmin": result = CssLengthUnit.Dvmin; return true;
            case "dvmax": result = CssLengthUnit.Dvmax; return true;
            default: return false;
        }
    }

    /// <summary>单位的规范小写写法。</summary>
    /// <remarks>查表而不用 <c>Enum.ToString()</c>（反射元数据 + 每次分配）。</remarks>
    public static string UnitText(CssLengthUnit unit) => (uint)unit < (uint)s_unitTexts.Length ? s_unitTexts[(int)unit] : string.Empty;

    // 与 CssLengthUnit 的声明顺序一一对应
    static readonly string[] s_unitTexts =
    {
        "px", "cm", "mm", "q", "in", "pt", "pc", "em", "rem", "ex", "ch", "vw", "vh", "vmin", "vmax", "vi", "vb",
        "svw", "svh", "svmin", "svmax", "lvw", "lvh", "lvmin", "lvmax", "dvw", "dvh", "dvmin", "dvmax", "%",
    };

    public bool Equals(CssLength other) => Value.Equals(other.Value) && Unit == other.Unit;
    public override bool Equals(object? obj) => obj is CssLength l && Equals(l);
    public override int GetHashCode() => HashCodeCompat.Combine(Value.GetHashCode(), (int)Unit);
    public static bool operator ==(CssLength a, CssLength b) => a.Equals(b);
    public static bool operator !=(CssLength a, CssLength b) => !a.Equals(b);

    public override string ToString() => CssSerializer.FormatNumber(Value) + UnitText(Unit);
}

/// <summary>角度与时间单位的换算。</summary>
public static class CssUnits
{
    /// <summary>把角度单位（deg、grad、rad、turn）换算为度。</summary>
    public static bool TryAngleToDegrees(double value, string unit, out double degrees)
    {
        degrees = 0;
        switch (CssSerializer.ToAsciiLower(unit))
        {
            case "deg": degrees = value; return true;
            case "grad": degrees = value * 0.9; return true;
            case "rad": degrees = value * 180 / Math.PI; return true;
            case "turn": degrees = value * 360; return true;
            default: return false;
        }
    }

    /// <summary>把时间单位（s、ms）换算为毫秒。</summary>
    public static bool TryTimeToMilliseconds(double value, string unit, out double milliseconds)
    {
        milliseconds = 0;
        switch (CssSerializer.ToAsciiLower(unit))
        {
            case "s": milliseconds = value * 1000; return true;
            case "ms": milliseconds = value; return true;
            default: return false;
        }
    }

    internal static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
