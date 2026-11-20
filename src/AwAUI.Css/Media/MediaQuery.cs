using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Media;

/// <summary>媒体查询前的修饰词。</summary>
public enum MediaQueryModifier
{
    None,
    Not,
    Only,
}

/// <summary>媒体条件节点的种类。</summary>
public enum MediaConditionKind
{
    Not,
    And,
    Or,
    Feature,
    /// <summary>语法上合法但无法识别的条件（未知特性、无效值、函数形式），求值为「未知」。</summary>
    GeneralEnclosed,
}

/// <summary>范围比较运算。</summary>
public enum MediaComparison
{
    Equal,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>Kleene 三值逻辑（Media Queries 4 §3.1）。</summary>
internal enum MediaTri : byte
{
    False,
    True,
    Unknown,
}

/// <summary>媒体查询列表（逗号分隔，任一成立即成立；空列表恒成立）。</summary>
public sealed class MediaQueryList
{
    /// <summary>空列表（恒成立）。</summary>
    public static readonly MediaQueryList Empty = new(Array.Empty<MediaQuery>());

    internal MediaQueryList(IReadOnlyList<MediaQuery> queries) => Queries = queries;

    public IReadOnlyList<MediaQuery> Queries { get; }

    /// <summary>在给定环境下是否成立。</summary>
    public bool Matches(MediaEnvironment environment)
    {
        if (environment is null) throw new ArgumentNullException(nameof(environment));
        if (Queries.Count == 0) return true;
        foreach (var q in Queries)
            if (q.Matches(environment)) return true;
        return false;
    }

    /// <summary>解析媒体查询列表文本，如 <c>screen and (min-width: 600px), print</c>。</summary>
    public static MediaQueryList Parse(string text, List<CssDiagnostic>? diagnostics = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var d = diagnostics ?? new List<CssDiagnostic>();
        var tokens = new CssParser(new CssSourceText(text), d);
        return MediaQueryParser.Parse(tokens.ConsumeAllComponentValues(), d);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Queries.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Queries[i]);
        }
        return sb.ToString();
    }
}

/// <summary>单条媒体查询：<c>[not|only]? 媒体类型 [and 条件]?</c> 或单独的条件。</summary>
public sealed class MediaQuery
{
    internal MediaQuery(MediaQueryModifier modifier, string? mediaType, MediaCondition? condition, bool invalid, SourceRange range)
    {
        Modifier = modifier;
        MediaType = mediaType;
        Condition = condition;
        IsInvalid = invalid;
        Range = range;
    }

    public MediaQueryModifier Modifier { get; }

    /// <summary>媒体类型（ASCII 小写）；只写了条件时为 null（等价于 all）。</summary>
    public string? MediaType { get; }

    public MediaCondition? Condition { get; }

    /// <summary>语法无效：按规范视为 <c>not all</c>，永不成立。</summary>
    public bool IsInvalid { get; }

    public SourceRange Range { get; }

    public bool Matches(MediaEnvironment environment)
    {
        if (IsInvalid) return false;
        var result = MediaTri.True;
        if (MediaType is not null)
        {
            bool typeMatches = MediaType == "all" || AsciiCase.EqualsIgnoreCase(MediaType, environment.MediaType);
            if (!typeMatches) result = MediaTri.False;
        }
        if (result == MediaTri.True && Condition is not null) result = Condition.Evaluate(environment);
        if (Modifier == MediaQueryModifier.Not) result = MediaCondition.Not(result);
        return result == MediaTri.True;
    }

    public override string ToString()
    {
        if (IsInvalid) return "not all";
        var sb = new StringBuilder();
        if (Modifier == MediaQueryModifier.Not) sb.Append("not ");
        else if (Modifier == MediaQueryModifier.Only) sb.Append("only ");
        if (MediaType is not null)
        {
            sb.Append(MediaType);
            if (Condition is not null) sb.Append(" and ");
        }
        if (Condition is not null) sb.Append(Condition);
        return sb.ToString();
    }
}

/// <summary>媒体条件树的节点。</summary>
public abstract class MediaCondition
{
    private protected MediaCondition()
    {
    }

    public abstract MediaConditionKind Kind { get; }

    /// <summary>在给定环境下求值；结果为「未知」时视为不成立。</summary>
    public bool Matches(MediaEnvironment environment) => Evaluate(environment) == MediaTri.True;

    internal abstract MediaTri Evaluate(MediaEnvironment environment);

    /// <summary>作为容器查询条件求值（<see cref="ContainerQueryInput"/> 给出查询容器的尺寸）。</summary>
    internal virtual MediaTri EvaluateContainer(in ContainerQueryInput input) => MediaTri.Unknown;

    /// <summary>容器查询条件需要的轴（宽度类特性 → 行内轴，高度类 → 块轴，比例与方向 → 两轴）。</summary>
    internal virtual ContainerAxes RequiredAxes => ContainerAxes.None;

    internal static MediaTri Not(MediaTri v) => v switch
    {
        MediaTri.True => MediaTri.False,
        MediaTri.False => MediaTri.True,
        _ => MediaTri.Unknown,
    };
}

/// <summary><c>not (…)</c></summary>
public sealed class MediaNotCondition : MediaCondition
{
    internal MediaNotCondition(MediaCondition operand) => Operand = operand;

    public override MediaConditionKind Kind => MediaConditionKind.Not;

    public MediaCondition Operand { get; }

    internal override MediaTri Evaluate(MediaEnvironment environment) => Not(Operand.Evaluate(environment));

    internal override MediaTri EvaluateContainer(in ContainerQueryInput input) => Not(Operand.EvaluateContainer(input));

    internal override ContainerAxes RequiredAxes => Operand.RequiredAxes;

    public override string ToString() => "not " + Paren(Operand);

    internal static string Paren(MediaCondition c)
        => c.Kind is MediaConditionKind.Feature or MediaConditionKind.GeneralEnclosed ? c.ToString()! : "(" + c + ")";
}

/// <summary><c>(…) and (…)</c> 或 <c>(…) or (…)</c>。</summary>
public sealed class MediaCombinedCondition : MediaCondition
{
    internal MediaCombinedCondition(bool isAnd, IReadOnlyList<MediaCondition> operands)
    {
        IsAnd = isAnd;
        Operands = operands;
    }

    public override MediaConditionKind Kind => IsAnd ? MediaConditionKind.And : MediaConditionKind.Or;

    public bool IsAnd { get; }

    public IReadOnlyList<MediaCondition> Operands { get; }

    internal override MediaTri Evaluate(MediaEnvironment environment)
    {
        bool unknown = false;
        foreach (var op in Operands)
        {
            var v = op.Evaluate(environment);
            if (IsAnd && v == MediaTri.False) return MediaTri.False;
            if (!IsAnd && v == MediaTri.True) return MediaTri.True;
            if (v == MediaTri.Unknown) unknown = true;
        }
        if (unknown) return MediaTri.Unknown;
        return IsAnd ? MediaTri.True : MediaTri.False;
    }

    internal override MediaTri EvaluateContainer(in ContainerQueryInput input)
    {
        bool unknown = false;
        foreach (var op in Operands)
        {
            var v = op.EvaluateContainer(input);
            if (IsAnd && v == MediaTri.False) return MediaTri.False;
            if (!IsAnd && v == MediaTri.True) return MediaTri.True;
            if (v == MediaTri.Unknown) unknown = true;
        }
        if (unknown) return MediaTri.Unknown;
        return IsAnd ? MediaTri.True : MediaTri.False;
    }

    internal override ContainerAxes RequiredAxes
    {
        get
        {
            var a = ContainerAxes.None;
            foreach (var op in Operands) a |= op.RequiredAxes;
            return a;
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Operands.Count; i++)
        {
            if (i > 0) sb.Append(IsAnd ? " and " : " or ");
            sb.Append(MediaNotCondition.Paren(Operands[i]));
        }
        return sb.ToString();
    }
}

/// <summary>无法识别的条件（未知特性、无效值、函数形式等）。求值恒为「未知」。</summary>
public sealed class MediaGeneralEnclosed : MediaCondition
{
    internal MediaGeneralEnclosed(string text) => Text = text;

    public override MediaConditionKind Kind => MediaConditionKind.GeneralEnclosed;

    /// <summary>原文（含括号）。</summary>
    public string Text { get; }

    internal override MediaTri Evaluate(MediaEnvironment environment) => MediaTri.Unknown;

    public override string ToString() => Text;
}

/// <summary>媒体特性的值种类。</summary>
public enum MediaValueKind
{
    Number,
    Dimension,
    Ident,
    Ratio,
}

/// <summary>媒体特性的值：数、带单位的数、关键字或比例。</summary>
public readonly struct MediaValue
{
    internal MediaValue(MediaValueKind kind, double number, string unit, string ident, double denominator)
    {
        Kind = kind;
        Number = number;
        Unit = unit;
        Ident = ident;
        Denominator = denominator;
    }

    public MediaValueKind Kind { get; }

    /// <summary>数值；比例时为分子。</summary>
    public double Number { get; }

    /// <summary>单位（ASCII 小写）。</summary>
    public string Unit { get; }

    /// <summary>关键字（ASCII 小写）。</summary>
    public string Ident { get; }

    /// <summary>比例的分母。</summary>
    public double Denominator { get; }

    public override string ToString() => Kind switch
    {
        MediaValueKind.Number => CssSerializer.FormatNumber(Number),
        MediaValueKind.Dimension => CssSerializer.FormatNumber(Number) + Unit,
        MediaValueKind.Ident => Ident,
        _ => CssSerializer.FormatNumber(Number) + " / " + CssSerializer.FormatNumber(Denominator),
    };
}

/// <summary>一条约束：「特性 运算 值」。</summary>
public readonly struct MediaFeatureConstraint
{
    internal MediaFeatureConstraint(MediaComparison comparison, MediaValue value)
    {
        Comparison = comparison;
        Value = value;
    }

    public MediaComparison Comparison { get; }

    public MediaValue Value { get; }
}

/// <summary>
/// 已识别的媒体特性测试。<c>(min-width: 600px)</c>、<c>(width &gt;= 600px)</c> 与 <c>(600px &lt;= width)</c>
/// 都规范化为约束「width ≥ 600px」；布尔形式 <c>(hover)</c> 没有约束。
/// </summary>
public sealed class MediaFeature : MediaCondition
{
    internal MediaFeature(string name, IReadOnlyList<MediaFeatureConstraint> constraints, string text)
    {
        Name = name;
        Constraints = constraints;
        Text = text;
    }

    public override MediaConditionKind Kind => MediaConditionKind.Feature;

    /// <summary>特性名（ASCII 小写，已去掉 min-/max- 前缀）。</summary>
    public string Name { get; }

    /// <summary>约束；为空表示布尔上下文。</summary>
    public IReadOnlyList<MediaFeatureConstraint> Constraints { get; }

    /// <summary>原文（含括号）。</summary>
    public string Text { get; }

    public bool IsBoolean => Constraints.Count == 0;

    public override string ToString() => Text;

    internal override MediaTri Evaluate(MediaEnvironment env)
    {
        switch (Name)
        {
            case "width": return Range(env.Width, ToPx);
            case "height": return Range(env.Height, ToPx);
            case "aspect-ratio":
                if (env.Height <= 0) return MediaTri.False;
                return Range(env.Width / env.Height, ToRatio);
            case "resolution": return Range(env.Resolution, ToDppx);
            case "orientation":
                return Discrete(env.Height >= env.Width ? "portrait" : "landscape", booleanTrue: true);
            case "prefers-color-scheme":
                return Discrete(env.ColorScheme == MediaColorScheme.Dark ? "dark" : "light", booleanTrue: true);
            case "prefers-reduced-motion":
                return Discrete(env.PrefersReducedMotion ? "reduce" : "no-preference", booleanTrue: env.PrefersReducedMotion);
            case "hover": return Discrete(env.Hover == MediaHoverCapability.Hover ? "hover" : "none", env.Hover != MediaHoverCapability.None);
            case "any-hover": return Discrete(env.AnyHover == MediaHoverCapability.Hover ? "hover" : "none", env.AnyHover != MediaHoverCapability.None);
            case "pointer": return Discrete(PointerName(env.Pointer), env.Pointer != MediaPointerCapability.None);
            case "any-pointer": return Discrete(PointerName(env.AnyPointer), env.AnyPointer != MediaPointerCapability.None);
            case "forced-colors": return Discrete(env.ForcedColors ? "active" : "none", env.ForcedColors);
            case "prefers-contrast":
                return Discrete(env.PrefersContrast switch
                {
                    MediaContrastPreference.More => "more",
                    MediaContrastPreference.Less => "less",
                    MediaContrastPreference.Custom => "custom",
                    _ => "no-preference",
                }, env.PrefersContrast != MediaContrastPreference.NoPreference);
            case "inverted-colors": return Discrete(env.InvertedColors ? "inverted" : "none", env.InvertedColors);
            case "scripting":
                return Discrete(env.Scripting switch { MediaScripting.None => "none", MediaScripting.InitialOnly => "initial-only", _ => "enabled" },
                    env.Scripting != MediaScripting.None);
            case "prefers-reduced-transparency":
                return Discrete(env.PrefersReducedTransparency ? "reduce" : "no-preference", env.PrefersReducedTransparency);
            case "prefers-reduced-data": return Discrete("no-preference", false);
            case "color-gamut":
                // 范围语义：设备色域覆盖查询的色域即成立
                if (Constraints.Count == 0) return MediaTri.True;
                foreach (var c in Constraints)
                {
                    int want = c.Value.Ident switch { "p3" => 1, "rec2020" => 2, _ => 0 };
                    if (want > (int)env.ColorGamut) return MediaTri.False;
                }
                return MediaTri.True;
            case "dynamic-range":
            case "video-dynamic-range":
                if (Constraints.Count == 0) return MediaTri.True;
                foreach (var c in Constraints)
                    if (c.Value.Ident == "high" && !env.HighDynamicRange) return MediaTri.False;
                return MediaTri.True;
            case "update": return Discrete("fast", true);
            case "display-mode": return Discrete(env.DisplayMode, true);
            default: return MediaTri.Unknown;
        }

        MediaTri Range(double actual, Func<MediaValue, MediaEnvironment, double> convert)
        {
            if (Constraints.Count == 0) return actual != 0 ? MediaTri.True : MediaTri.False;
            foreach (var c in Constraints)
            {
                double v = convert(c.Value, env);
                if (double.IsNaN(v)) return MediaTri.False;
                bool ok = c.Comparison switch
                {
                    MediaComparison.Equal => actual == v,
                    MediaComparison.Less => actual < v,
                    MediaComparison.LessOrEqual => actual <= v,
                    MediaComparison.Greater => actual > v,
                    _ => actual >= v,
                };
                if (!ok) return MediaTri.False;
            }
            return MediaTri.True;
        }

        MediaTri Discrete(string actual, bool booleanTrue)
        {
            if (Constraints.Count == 0) return booleanTrue ? MediaTri.True : MediaTri.False;
            foreach (var c in Constraints)
                if (c.Value.Ident != actual) return MediaTri.False;
            return MediaTri.True;
        }
    }

    internal override ContainerAxes RequiredAxes => Name switch
    {
        "width" or "inline-size" => ContainerAxes.Inline,
        "height" or "block-size" => ContainerAxes.Block,
        "aspect-ratio" or "orientation" => ContainerAxes.Both,
        _ => ContainerAxes.None,
    };

    internal override MediaTri EvaluateContainer(in ContainerQueryInput input)
    {
        var q = input;
        switch (Name)
        {
            case "width":
            case "inline-size":
                return q.HasWidth ? Range(q.Width, q) : MediaTri.Unknown;
            case "height":
            case "block-size":
                return q.HasHeight ? Range(q.Height, q) : MediaTri.Unknown;
            case "aspect-ratio":
                if (!q.HasWidth || !q.HasHeight || q.Height <= 0) return MediaTri.Unknown;
                return RangeRatio(q.Width / q.Height);
            case "orientation":
                if (!q.HasWidth || !q.HasHeight) return MediaTri.Unknown;
                if (Constraints.Count == 0) return MediaTri.True;
                foreach (var c in Constraints)
                    if (c.Value.Ident != (q.Height >= q.Width ? "portrait" : "landscape")) return MediaTri.False;
                return MediaTri.True;
            default:
                return MediaTri.Unknown;
        }

        MediaTri Range(double actual, in ContainerQueryInput ctx)
        {
            if (Constraints.Count == 0) return actual != 0 ? MediaTri.True : MediaTri.False;
            foreach (var c in Constraints)
            {
                double v = ContainerPx(c.Value, ctx);
                if (double.IsNaN(v)) return MediaTri.Unknown;
                if (!Compare(actual, c.Comparison, v)) return MediaTri.False;
            }
            return MediaTri.True;
        }

        MediaTri RangeRatio(double actual)
        {
            if (Constraints.Count == 0) return actual != 0 ? MediaTri.True : MediaTri.False;
            foreach (var c in Constraints)
            {
                double v = ToRatio(c.Value, null!);
                if (double.IsNaN(v)) return MediaTri.Unknown;
                if (!Compare(actual, c.Comparison, v)) return MediaTri.False;
            }
            return MediaTri.True;
        }
    }

    static bool Compare(double actual, MediaComparison op, double v) => op switch
    {
        MediaComparison.Equal => actual == v,
        MediaComparison.Less => actual < v,
        MediaComparison.LessOrEqual => actual <= v,
        MediaComparison.Greater => actual > v,
        _ => actual >= v,
    };

    /// <summary>容器查询中的长度：em 相对查询容器的字体大小，rem 相对根元素，视口单位相对视口。</summary>
    static double ContainerPx(MediaValue v, in ContainerQueryInput q)
    {
        if (v.Kind == MediaValueKind.Number) return v.Number == 0 ? 0 : double.NaN;
        if (v.Kind != MediaValueKind.Dimension) return double.NaN;
        switch (v.Unit)
        {
            case "em": return v.Number * q.FontSize;
            case "rem": return v.Number * q.RootFontSize;
            case "ex":
            case "ch": return v.Number * q.FontSize / 2;
        }
        double f = LengthFactor(v.Unit, q.Media);
        return double.IsNaN(f) ? f : v.Number * f;
    }

    static string PointerName(MediaPointerCapability p) => p switch
    {
        MediaPointerCapability.Fine => "fine",
        MediaPointerCapability.Coarse => "coarse",
        _ => "none",
    };

    internal static double ToPx(MediaValue v, MediaEnvironment env)
    {
        if (v.Kind == MediaValueKind.Number) return v.Number == 0 ? 0 : double.NaN;
        if (v.Kind != MediaValueKind.Dimension) return double.NaN;
        double f = LengthFactor(v.Unit, env);
        return double.IsNaN(f) ? f : v.Number * f;
    }

    internal static double LengthFactor(string unit, MediaEnvironment? env)
    {
        double fs = env?.FontSize ?? 16, w = env?.Width ?? 0, h = env?.Height ?? 0;
        return unit switch
        {
            "px" => 1,
            "em" or "rem" => fs,
            "ex" or "ch" => fs / 2,
            "in" => 96,
            "cm" => 96 / 2.54,
            "mm" => 96 / 25.4,
            "q" => 96 / 101.6,
            "pt" => 96.0 / 72,
            "pc" => 16,
            "vw" => w / 100,
            "vh" => h / 100,
            "vmin" => Math.Min(w, h) / 100,
            "vmax" => Math.Max(w, h) / 100,
            _ => double.NaN,
        };
    }

    internal static double ToDppx(MediaValue v, MediaEnvironment env)
    {
        if (v.Kind == MediaValueKind.Ident) return v.Ident == "infinite" ? double.PositiveInfinity : double.NaN;
        if (v.Kind != MediaValueKind.Dimension) return double.NaN;
        double f = ResolutionFactor(v.Unit);
        return double.IsNaN(f) ? f : v.Number * f;
    }

    internal static double ResolutionFactor(string unit) => unit switch
    {
        "dppx" or "x" => 1,
        "dpi" => 1.0 / 96,
        "dpcm" => 2.54 / 96,
        _ => double.NaN,
    };

    internal static double ToRatio(MediaValue v, MediaEnvironment env)
    {
        if (v.Kind == MediaValueKind.Number) return v.Number;
        if (v.Kind != MediaValueKind.Ratio) return double.NaN;
        if (v.Denominator == 0) return v.Number == 0 ? double.NaN : double.PositiveInfinity;
        return v.Number / v.Denominator;
    }

    internal static string Format(double d) => d.ToString(CultureInfo.InvariantCulture);
}

/// <summary>容器查询需要的轴。</summary>
[Flags]
internal enum ContainerAxes : byte
{
    None = 0,
    Inline = 1,
    Block = 2,
    Both = 3,
}

/// <summary>容器查询求值的输入：查询容器的内容盒尺寸（未知的轴为 NaN）与字体大小。</summary>
internal readonly struct ContainerQueryInput
{
    public ContainerQueryInput(double width, double height, double fontSize, double rootFontSize, MediaEnvironment media)
    {
        Width = width;
        Height = height;
        FontSize = fontSize;
        RootFontSize = rootFontSize;
        Media = media;
    }

    public double Width { get; }
    public double Height { get; }
    public bool HasWidth => !double.IsNaN(Width);
    public bool HasHeight => !double.IsNaN(Height);
    public double FontSize { get; }
    public double RootFontSize { get; }
    public MediaEnvironment Media { get; }
}

/// <summary>prefers-contrast 的取值。</summary>
public enum MediaContrastPreference
{
    NoPreference,
    More,
    Less,
    Custom,
}

/// <summary>scripting 的取值。</summary>
public enum MediaScripting
{
    None,
    InitialOnly,
    Enabled,
}

/// <summary>color-gamut：设备大致覆盖的色域。</summary>
public enum MediaColorGamut
{
    Srgb,
    P3,
    Rec2020,
}

/// <summary>prefers-color-scheme 的取值。</summary>
public enum MediaColorScheme
{
    Light,
    Dark,
}

/// <summary>hover / any-hover 的取值。</summary>
public enum MediaHoverCapability
{
    None,
    Hover,
}

/// <summary>pointer / any-pointer 的取值。</summary>
public enum MediaPointerCapability
{
    None,
    Coarse,
    Fine,
}

/// <summary>媒体查询求值环境。长度单位为逻辑像素（CSS px）。</summary>
public sealed class MediaEnvironment
{
    /// <summary>媒体类型，默认 <c>screen</c>。</summary>
    public string MediaType { get; set; } = "screen";

    /// <summary>视口宽度（CSS px）。</summary>
    public double Width { get; set; } = 1024;

    /// <summary>视口高度（CSS px）。</summary>
    public double Height { get; set; } = 768;

    /// <summary>设备像素比（dppx）。</summary>
    public double Resolution { get; set; } = 1;

    /// <summary>em/rem 在媒体查询中的基准（初始字体大小）。</summary>
    public double FontSize { get; set; } = 16;

    public MediaColorScheme ColorScheme { get; set; } = MediaColorScheme.Light;

    public bool PrefersReducedMotion { get; set; }

    public MediaHoverCapability Hover { get; set; } = MediaHoverCapability.Hover;

    public MediaHoverCapability AnyHover { get; set; } = MediaHoverCapability.Hover;

    public MediaPointerCapability Pointer { get; set; } = MediaPointerCapability.Fine;

    public MediaPointerCapability AnyPointer { get; set; } = MediaPointerCapability.Fine;

    /// <summary><c>forced-colors: active</c>（Windows 高对比度等强制颜色模式）。</summary>
    public bool ForcedColors { get; set; }

    /// <summary><c>prefers-contrast</c>。</summary>
    public MediaContrastPreference PrefersContrast { get; set; }

    /// <summary><c>inverted-colors: inverted</c>。</summary>
    public bool InvertedColors { get; set; }

    /// <summary><c>scripting</c>：应用中脚本（C# 代码）总是可用，默认 enabled。</summary>
    public MediaScripting Scripting { get; set; } = MediaScripting.Enabled;

    /// <summary><c>prefers-reduced-transparency: reduce</c>。</summary>
    public bool PrefersReducedTransparency { get; set; }

    /// <summary><c>color-gamut</c>：显示器色域，默认 sRGB。</summary>
    public MediaColorGamut ColorGamut { get; set; }

    /// <summary><c>dynamic-range: high</c>。</summary>
    public bool HighDynamicRange { get; set; }

    /// <summary><c>display-mode</c> 的值（ASCII 小写），桌面应用默认 <c>standalone</c>。</summary>
    public string DisplayMode { get; set; } = "standalone";
}
