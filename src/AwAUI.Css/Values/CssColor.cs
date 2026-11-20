using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>
/// 颜色的计算值：具体的 sRGB 颜色、保留为关键字的 <c>currentColor</c>（使用时取元素的 <c>color</c>，design/08），
/// 或引用了 <c>currentColor</c> 的 <c>color-mix()</c>（在使用值阶段按元素的 <c>color</c> 求值，CSS Color 5 §2）。
/// <para>
/// 所有颜色函数（<c>oklch()</c>、<c>lab()</c>、<c>color(display-p3 …)</c> 等）在解析时以双精度换算为 sRGB 并按分量裁剪
/// （与浏览器的实际渲染一致，见 <see cref="CssColorMath"/>），<see cref="Value"/> 是单精度的结果。
/// </para>
/// </summary>
public readonly struct CssColor : IEquatable<CssColor>
{
    readonly Color _value;
    readonly byte _kind; // 0 = 具体颜色，1 = currentColor，2 = 延迟求值的 color-mix
    readonly CssColorMixExpression? _mix;

    public CssColor(Color value)
    {
        _value = value;
        _kind = 0;
        _mix = null;
    }

    CssColor(byte kind, CssColorMixExpression? mix)
    {
        _value = default;
        _kind = kind;
        _mix = mix;
    }

    internal static CssColor Deferred(CssColorMixExpression mix) => new(2, mix);

    /// <summary>延迟求值的 <c>color-mix()</c>（种类 2）；其余为 null。</summary>
    internal CssColorMixExpression? MixExpression => _mix;

    /// <summary>转为解析期的颜色表示（具体颜色按 sRGB）。</summary>
    internal ParsedColor ToParsed() => _kind switch
    {
        0 => ParsedColor.Of(WideColor.FromSrgb(_value)),
        1 => ParsedColor.Current,
        _ => ParsedColor.Of(_mix!),
    };

    /// <summary><c>currentColor</c>。</summary>
    public static CssColor CurrentColor => new(1, null);

    public static CssColor Transparent => new(Color.Transparent);

    /// <summary>具体颜色；<see cref="IsCurrentColor"/> 或 <see cref="DependsOnCurrentColor"/> 为真时无意义（为透明）。</summary>
    public Color Value => _value;

    /// <summary>是否为 <c>currentColor</c> 关键字本身。</summary>
    public bool IsCurrentColor => _kind == 1;

    /// <summary>值是否依赖元素的 <c>color</c>（<c>currentColor</c> 或含 <c>currentColor</c> 的 <c>color-mix()</c>）。</summary>
    public bool DependsOnCurrentColor => _kind != 0;

    /// <summary>得到使用值：<c>currentColor</c> 取 <paramref name="currentColor"/>；含 <c>currentColor</c> 的 <c>color-mix()</c> 在此时求值。</summary>
    public Color Resolve(Color currentColor) => _kind switch
    {
        0 => _value,
        1 => currentColor,
        _ => _mix!.Resolve(currentColor),
    };

    public bool Equals(CssColor other)
        => _kind == other._kind && _value.Equals(other._value) && (_kind != 2 || _mix!.Equals(other._mix));

    public override bool Equals(object? obj) => obj is CssColor c && Equals(c);
    public override int GetHashCode() => _kind == 0 ? _value.GetHashCode() : _kind == 1 ? 1 : _mix!.GetHashCode();
    public static bool operator ==(CssColor a, CssColor b) => a.Equals(b);
    public static bool operator !=(CssColor a, CssColor b) => !a.Equals(b);

    /// <summary>按 CSSOM 的规则序列化：不透明为 <c>rgb(r, g, b)</c>，否则 <c>rgba(r, g, b, a)</c>；延迟的 <c>color-mix()</c> 按其源文本。</summary>
    public override string ToString()
    {
        if (_kind == 1) return "currentcolor";
        if (_kind == 2) return _mix!.Text;
        var c = _value;
        int r = To8(c.R), g = To8(c.G), b = To8(c.B);
        if (c.A >= 1f) return string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", r, g, b);
        double a = Math.Round(Math.Max(0, Math.Min(1, (double)c.A)), 3);
        return string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3})", r, g, b, CssSerializer.FormatNumber(a));
    }

    static int To8(float v) => (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
}

/// <summary>
/// 引用了 <c>currentColor</c> 的 <c>color-mix()</c>：保留插值空间、两个操作数与权重，在使用值阶段求值。
/// 不含 <c>currentColor</c> 的操作数保留为未裁剪的双精度颜色（嵌套的混合也一样），所以与浏览器一样只在最后一步裁剪。
/// </summary>
internal sealed class CssColorMixExpression : IEquatable<CssColorMixExpression>
{
    public CssColorMixExpression(CssColorSpace space, CssHueInterpolation hue, ParsedColor a, ParsedColor b, double t, double alphaMultiplier, string text)
    {
        Space = space;
        Hue = hue;
        A = a;
        B = b;
        T = t;
        AlphaMultiplier = alphaMultiplier;
        Text = text;
    }

    public CssColorSpace Space { get; }
    public CssHueInterpolation Hue { get; }
    public ParsedColor A { get; }
    public ParsedColor B { get; }

    /// <summary>第二个颜色的权重（0–1）。</summary>
    public double T { get; }

    /// <summary>百分比之和小于 100% 时的 alpha 乘数。</summary>
    public double AlphaMultiplier { get; }

    /// <summary>源文本（序列化用）。</summary>
    public string Text { get; }

    public Color Resolve(Color currentColor) => CssColorMath.ToSrgb(ResolveWide(currentColor));

    internal WideColor ResolveWide(Color currentColor)
    {
        var a = A.ResolveWide(currentColor);
        var b = B.ResolveWide(currentColor);
        return CssColorParser.Mix(a, b, T, AlphaMultiplier, Space, Hue);
    }

    public bool Equals(CssColorMixExpression? other)
        => other is not null && (ReferenceEquals(this, other)
            || (Space == other.Space && Hue == other.Hue && T == other.T && AlphaMultiplier == other.AlphaMultiplier && A.Equals(other.A) && B.Equals(other.B)));

    public override bool Equals(object? obj) => obj is CssColorMixExpression m && Equals(m);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Space, T.GetHashCode(), A.GetHashCode(), B.GetHashCode());
}

/// <summary>解析得到的颜色：具体颜色（任意空间、未裁剪）、<c>currentColor</c>，或延迟求值的 <c>color-mix()</c>。</summary>
internal readonly struct ParsedColor : IEquatable<ParsedColor>
{
    ParsedColor(byte kind, WideColor wide, CssColorMixExpression? mix)
    {
        Kind = kind;
        Wide = wide;
        Mix = mix;
    }

    public static ParsedColor Of(WideColor wide) => new(0, wide, null);
    public static readonly ParsedColor Current = new(1, default, null);
    public static ParsedColor Of(CssColorMixExpression mix) => new(2, default, mix);

    /// <summary>0 = 具体颜色，1 = currentColor，2 = 延迟的混合。</summary>
    public byte Kind { get; }
    public WideColor Wide { get; }
    public CssColorMixExpression? Mix { get; }

    public WideColor ResolveWide(Color currentColor) => Kind switch
    {
        0 => Wide,
        1 => WideColor.FromSrgb(currentColor),
        _ => Mix!.ResolveWide(currentColor),
    };

    public CssColor ToCssColor() => Kind switch
    {
        0 => new CssColor(CssColorMath.ToSrgb(Wide)),
        1 => CssColor.CurrentColor,
        _ => CssColor.Deferred(Mix!),
    };

    public bool Equals(ParsedColor o) => Kind == o.Kind && Wide.Equals(o.Wide) && (Kind != 2 || Mix!.Equals(o.Mix));
    public override bool Equals(object? obj) => obj is ParsedColor p && Equals(p);
    public override int GetHashCode() => Kind == 0 ? Wide.GetHashCode() : Kind == 1 ? 1 : Mix!.GetHashCode();
}

/// <summary>
/// 颜色解析（CSS Color 4/5）：十六进制、命名色、<c>transparent</c>、<c>currentColor</c>、<c>rgb()/rgba()/hsl()/hsla()</c>（逗号与空格语法）、
/// <c>hwb()</c>、<c>lab()</c>、<c>lch()</c>、<c>oklab()</c>、<c>oklch()</c>、<c>color()</c>（srgb、srgb-linear、display-p3、a98-rgb、
/// prophoto-rgb、rec2020、xyz、xyz-d50、xyz-d65）、<c>color-mix()</c>（全部插值空间与色相插值方法）、<c>none</c> 分量，
/// 以及相对颜色语法 <c>rgb(from &lt;color&gt; r g b)</c>（通道关键字可出现在 <c>calc()</c> 中；来源颜色不能依赖 <c>currentColor</c>）。
/// </summary>
public static class CssColorParser
{
    /// <summary>颜色函数（color-mix 与相对颜色）的最大嵌套深度。</summary>
    const int MaxDepth = 16;

    /// <summary>解析颜色文本（如 <c>#fff</c>、<c>rgb(0 0 0 / 50%)</c>）。</summary>
    public static bool TryParse(string text, out CssColor color)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var values = CssComponentValue.ParseList(text);
        var r = new ValueReader(values);
        color = default;
        var v = r.Next();
        if (v is null || !r.AtEnd) return false;
        return TryParse(v, out color);
    }

    /// <summary>解析单个组件值为颜色。</summary>
    public static bool TryParse(CssComponentValue value, out CssColor color)
    {
        if (TryParseColor(value, 0, out var pc))
        {
            color = pc.ToCssColor();
            return true;
        }
        color = default;
        return false;
    }

    /// <summary>不做裁剪的解析结果（内部：color-mix、注册属性等使用）。</summary>
    internal static bool TryParseColor(CssComponentValue value, int depth, out ParsedColor color)
    {
        color = default;
        if (depth > MaxDepth) return false;
        switch (value)
        {
            case CssToken { Type: CssTokenType.Hash } h:
                if (!TryParseHex(h.Value, out var c)) return false;
                color = ParsedColor.Of(WideColor.FromSrgb(c));
                return true;
            case CssToken { Type: CssTokenType.Ident } id:
            {
                var name = CssSerializer.ToAsciiLower(id.Value);
                if (name == "currentcolor") { color = ParsedColor.Current; return true; }
                if (name == "transparent") { color = ParsedColor.Of(new WideColor(CssColorSpace.Srgb, 0, 0, 0, 0)); return true; }
                if (NamedColors.TryGetValue(name, out var rgb))
                {
                    color = ParsedColor.Of(new WideColor(CssColorSpace.Srgb, ((rgb >> 16) & 255) / 255.0, ((rgb >> 8) & 255) / 255.0, (rgb & 255) / 255.0, 1));
                    return true;
                }
                return false;
            }
            case CssFunction f:
            {
                var fn = CssSerializer.ToAsciiLower(f.Name);
                switch (fn)
                {
                    case "rgb":
                    case "rgba":
                        return TryParseFunction(f.Arguments, CssColorSpace.Srgb, legacyAllowed: true, depth, out color);
                    case "hsl":
                    case "hsla":
                        return TryParseFunction(f.Arguments, CssColorSpace.Hsl, legacyAllowed: true, depth, out color);
                    case "hwb": return TryParseFunction(f.Arguments, CssColorSpace.Hwb, legacyAllowed: false, depth, out color);
                    case "lab": return TryParseFunction(f.Arguments, CssColorSpace.Lab, legacyAllowed: false, depth, out color);
                    case "lch": return TryParseFunction(f.Arguments, CssColorSpace.Lch, legacyAllowed: false, depth, out color);
                    case "oklab": return TryParseFunction(f.Arguments, CssColorSpace.Oklab, legacyAllowed: false, depth, out color);
                    case "oklch": return TryParseFunction(f.Arguments, CssColorSpace.Oklch, legacyAllowed: false, depth, out color);
                    case "color": return TryParseColorFunction(f.Arguments, depth, out color);
                    case "color-mix": return TryParseColorMix(f, depth, out color);
                    default: return false;
                }
            }
            default:
                return false;
        }
    }

    /// <summary>hex 记号的值（不含 #）：3、4、6 或 8 位十六进制。</summary>
    internal static bool TryParseHex(string hex, out Color color)
    {
        color = default;
        int n = hex.Length;
        if (n is not (3 or 4 or 6 or 8)) return false;
        Span<int> d = stackalloc int[8];
        for (int i = 0; i < n; i++)
        {
            int v = HexValue(hex[i]);
            if (v < 0) return false;
            d[i] = v;
        }
        int r, g, b, a = 255;
        if (n <= 4)
        {
            r = d[0] * 17; g = d[1] * 17; b = d[2] * 17;
            if (n == 4) a = d[3] * 17;
        }
        else
        {
            r = d[0] * 16 + d[1]; g = d[2] * 16 + d[3]; b = d[4] * 16 + d[5];
            if (n == 8) a = d[6] * 16 + d[7];
        }
        color = Color.FromRgba((byte)r, (byte)g, (byte)b, (byte)a);
        return true;
    }

    static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    // ================= 分量 =================

    /// <summary>颜色函数的一个分量：数、百分比、角度或 <c>none</c>。</summary>
    enum ComponentKind { Number, Percent, None, Angle }

    readonly struct Component
    {
        public Component(ComponentKind kind, double value)
        {
            Kind = kind;
            Value = value;
        }

        public ComponentKind Kind { get; }
        public double Value { get; }
    }

    /// <summary>相对颜色语法中的通道关键字 → 值（来源颜色已换算到目标空间）。</summary>
    sealed class ChannelScope
    {
        public readonly Dictionary<string, double> Values = new(StringComparer.Ordinal);
    }

    /// <summary>把参数拆成分量：返回 (分量列表, 是否逗号语法, alpha)。calc() 在分量中按数值求值。</summary>
    static bool TrySplit(IReadOnlyList<CssComponentValue> args, int start, List<Component> comps, ChannelScope? scope, out bool legacy, out Component? alpha)
    {
        legacy = false;
        alpha = null;
        var r = new ValueReader(args) { Position = start };
        bool sawComma = false, sawSlash = false;
        int index = 0;
        while (!r.AtEnd)
        {
            if (index > 0)
            {
                if (r.TryComma())
                {
                    if (index == 1) sawComma = true;
                    else if (!sawComma) return false;
                }
                else if (sawComma) return false;
                if (!sawComma && r.TryDelim('/'))
                {
                    if (sawSlash || index != 3) return false;
                    sawSlash = true;
                }
            }
            var v = r.Next();
            if (v is null) return false;
            if (!TryComponent(v, scope, out var c)) return false;
            if (sawSlash) { if (alpha.HasValue) return false; alpha = c; }
            else if (sawComma && index == 3) alpha = c;
            else comps.Add(c);
            index++;
            if (index > 4) return false;
        }
        legacy = sawComma;
        return comps.Count == 3;
    }

    static bool TryComponent(CssComponentValue v, ChannelScope? scope, out Component c)
    {
        c = default;
        switch (v)
        {
            case CssToken { Type: CssTokenType.Number } n: c = new Component(ComponentKind.Number, n.Number); return true;
            case CssToken { Type: CssTokenType.Percentage } p: c = new Component(ComponentKind.Percent, p.Number); return true;
            case CssToken { Type: CssTokenType.Dimension } d:
                if (CssUnits.TryAngleToDegrees(d.Number, d.Unit, out var deg)) { c = new Component(ComponentKind.Angle, deg); return true; }
                return false;
            case CssToken { Type: CssTokenType.Ident } id when id.IsIdent("none"):
                c = new Component(ComponentKind.None, 0);
                return true;
            case CssToken { Type: CssTokenType.Ident } id when scope is not null:
                if (scope.Values.TryGetValue(CssSerializer.ToAsciiLower(id.Value), out var ch)) { c = new Component(ComponentKind.Number, ch); return true; }
                return false;
            case CssFunction f when CalcParser.IsMathFunction(f.Name):
            {
                var fn = scope is null ? f : (CssFunction)SubstituteChannels(f, scope, 0);
                var node = CalcParser.ParseComponent(fn, PercentMode.Length, allowUnitlessZero: false);
                if (node is null || node.IsContextDependent) return false;
                var res = node.Compute(new LengthContext(16, 16, DefaultEnvironment));
                switch (node.Category)
                {
                    case CalcCategory.Number: c = new Component(ComponentKind.Number, res.Number); return true;
                    case CalcCategory.Angle: c = new Component(ComponentKind.Angle, res.Number); return true;
                    case CalcCategory.Percent when res.Tree is null: c = new Component(ComponentKind.Percent, res.Pct); return true;
                    default: return false;
                }
            }
            default:
                return false;
        }
    }

    /// <summary>把 calc() 中的通道关键字替换为 number 记号。</summary>
    static CssComponentValue SubstituteChannels(CssComponentValue v, ChannelScope scope, int depth)
    {
        switch (v)
        {
            case CssToken { Type: CssTokenType.Ident } id when depth < 64 && scope.Values.TryGetValue(CssSerializer.ToAsciiLower(id.Value), out var ch):
                return CssToken.CreateNumber(ch);
            case CssFunction f when depth < 64:
            {
                var args = new List<CssComponentValue>(f.Arguments.Count);
                foreach (var a in f.Arguments) args.Add(SubstituteChannels(a, scope, depth + 1));
                return new CssFunction(f.Name, args, f.Range, f.IsClosed);
            }
            case CssSimpleBlock b when depth < 64:
            {
                var vals = new List<CssComponentValue>(b.Values.Count);
                foreach (var a in b.Values) vals.Add(SubstituteChannels(a, scope, depth + 1));
                return new CssSimpleBlock(b.Open, vals, b.Range, b.IsClosed);
            }
            default:
                return v;
        }
    }

    static readonly CssEnvironment DefaultEnvironment = new();

    static bool TryAlpha(Component? alpha, out double a, out bool missing)
    {
        a = 1;
        missing = false;
        if (alpha is not { } c) return true;
        switch (c.Kind)
        {
            case ComponentKind.Number: a = c.Value; break;
            case ComponentKind.Percent: a = c.Value / 100; break;
            case ComponentKind.None: a = 0; missing = true; break;
            default: return false;
        }
        a = CalcMath.Clamp(CalcMath.FiniteDouble(a), 0, 1);
        return true;
    }

    /// <summary>
    /// 一个分量的值。<paramref name="percentScale"/> 是 100% 对应的数值；<paramref name="hue"/> 表示色相分量（数或角度，不接受百分比）。
    /// 返回 false 表示类型错误。
    /// </summary>
    static bool Channel(Component c, double percentScale, bool hue, bool allowNumber, out double value, out bool missing)
    {
        value = 0;
        missing = false;
        switch (c.Kind)
        {
            case ComponentKind.None: missing = true; return true;
            case ComponentKind.Number:
                if (!allowNumber && !hue) return false;
                value = CalcMath.FiniteDouble(c.Value);
                return true;
            case ComponentKind.Percent:
                if (hue) return false;
                value = CalcMath.FiniteDouble(c.Value) / 100 * percentScale;
                return true;
            case ComponentKind.Angle:
                if (!hue) return false;
                value = CalcMath.FiniteDouble(c.Value);
                return true;
        }
        return false;
    }

    /// <summary>rgb/hsl/hwb/lab/lch/oklab/oklch：含相对颜色语法（<c>from</c>）。</summary>
    static bool TryParseFunction(IReadOnlyList<CssComponentValue> args, CssColorSpace space, bool legacyAllowed, int depth, out ParsedColor color)
    {
        color = default;
        int start = 0;
        ChannelScope? scope = null;
        var r = new ValueReader(args);
        if (r.TryIdent("from"))
        {
            var originValue = r.Next();
            if (originValue is null || !TryParseColor(originValue, depth + 1, out var origin) || origin.Kind != 0) return false;
            scope = CreateScope(origin.Wide, space);
            start = r.Position;
        }
        var comps = new List<Component>(3);
        if (!TrySplit(args, start, comps, scope, out bool legacy, out var alpha)) return false;
        if (scope is not null && alpha is null) alpha = new Component(ComponentKind.Number, scope.Values["alpha"]);
        if (legacy)
        {
            if (!legacyAllowed || scope is not null) return false;
            if (alpha is { Kind: ComponentKind.None }) return false;
            foreach (var c in comps) if (c.Kind == ComponentKind.None) return false;
            if (space == CssColorSpace.Srgb)
            {
                // 逗号语法：三个分量必须同为数或同为百分比
                var k = comps[0].Kind;
                if (k == ComponentKind.Angle) return false;
                foreach (var c in comps) if (c.Kind != k) return false;
            }
            else if (comps[1].Kind != ComponentKind.Percent || comps[2].Kind != ComponentKind.Percent) return false;
        }
        if (!TryAlpha(alpha, out var a, out bool alphaMissing)) return false;
        double v0, v1, v2;
        bool m0, m1, m2;
        switch (space)
        {
            case CssColorSpace.Srgb:
                if (!Channel(comps[0], 255, false, true, out v0, out m0) || !Channel(comps[1], 255, false, true, out v1, out m1)
                    || !Channel(comps[2], 255, false, true, out v2, out m2)) return false;
                // 相对颜色语法不钳（与 Chrome 一致：结果是可超出 sRGB 的 color(srgb …)）
                if (scope is null)
                {
                    v0 = CalcMath.Clamp(v0, 0, 255);
                    v1 = CalcMath.Clamp(v1, 0, 255);
                    v2 = CalcMath.Clamp(v2, 0, 255);
                }
                v0 /= 255;
                v1 /= 255;
                v2 /= 255;
                break;
            case CssColorSpace.Hsl:
            case CssColorSpace.Hwb:
                if (!Channel(comps[0], 0, true, true, out v0, out m0) || !Channel(comps[1], 100, false, true, out v1, out m1)
                    || !Channel(comps[2], 100, false, true, out v2, out m2)) return false;
                v0 = NormalizeHue(v0);
                // 与 Chrome 一致：旧（逗号）语法钳到 [0, 100]；现代语法只钳下界（上界保留，在 color-mix 中可观察到）；
                // 相对颜色语法不钳（广色域来源的饱和度可超过 100%、可为负，换算回 sRGB 时可往返）
                if (scope is null)
                {
                    v1 = legacy ? CalcMath.Clamp(v1, 0, 100) : Math.Max(0, v1);
                    v2 = legacy ? CalcMath.Clamp(v2, 0, 100) : Math.Max(0, v2);
                }
                break;
            case CssColorSpace.Lab:
                if (!Channel(comps[0], 100, false, true, out v0, out m0) || !Channel(comps[1], 125, false, true, out v1, out m1)
                    || !Channel(comps[2], 125, false, true, out v2, out m2)) return false;
                v0 = CalcMath.Clamp(v0, 0, 100);
                break;
            case CssColorSpace.Lch:
                if (!Channel(comps[0], 100, false, true, out v0, out m0) || !Channel(comps[1], 150, false, true, out v1, out m1)
                    || !Channel(comps[2], 0, true, true, out v2, out m2)) return false;
                v0 = CalcMath.Clamp(v0, 0, 100);
                v1 = Math.Max(0, v1);
                v2 = NormalizeHue(v2);
                break;
            case CssColorSpace.Oklab:
                if (!Channel(comps[0], 1, false, true, out v0, out m0) || !Channel(comps[1], 0.4, false, true, out v1, out m1)
                    || !Channel(comps[2], 0.4, false, true, out v2, out m2)) return false;
                v0 = CalcMath.Clamp(v0, 0, 1);
                break;
            default: // Oklch
                if (!Channel(comps[0], 1, false, true, out v0, out m0) || !Channel(comps[1], 0.4, false, true, out v1, out m1)
                    || !Channel(comps[2], 0, true, true, out v2, out m2)) return false;
                v0 = CalcMath.Clamp(v0, 0, 1);
                v1 = Math.Max(0, v1);
                v2 = NormalizeHue(v2);
                break;
        }
        // hsl() 的旧语法与 rgb() 一样，number 分量不允许出现在百分比的位置（逗号语法已在上面检查）
        if (space == CssColorSpace.Hsl && comps[0].Kind == ComponentKind.Percent) return false;
        byte missing = (byte)((m0 ? 1 : 0) | (m1 ? 2 : 0) | (m2 ? 4 : 0) | (alphaMissing ? 8 : 0));
        color = ParsedColor.Of(new WideColor(space, v0, v1, v2, a, missing));
        return true;
    }

    /// <summary>相对颜色语法：来源颜色换算到目标空间后的通道值（缺失分量为 0）。</summary>
    static ChannelScope CreateScope(in WideColor origin, CssColorSpace space)
    {
        var w = CssColorMath.ConvertWide(origin, space);
        double V(int i) => w.IsMissing(i) ? 0 : w[i];
        var s = new ChannelScope();
        switch (space)
        {
            case CssColorSpace.Srgb:
                s.Values["r"] = V(0) * 255; s.Values["g"] = V(1) * 255; s.Values["b"] = V(2) * 255; break;
            case CssColorSpace.Hsl:
                s.Values["h"] = V(0); s.Values["s"] = V(1); s.Values["l"] = V(2); break;
            case CssColorSpace.Hwb:
                s.Values["h"] = V(0); s.Values["w"] = V(1); s.Values["b"] = V(2); break;
            case CssColorSpace.Lab:
            case CssColorSpace.Oklab:
                s.Values["l"] = V(0); s.Values["a"] = V(1); s.Values["b"] = V(2); break;
            case CssColorSpace.Lch:
            case CssColorSpace.Oklch:
                s.Values["l"] = V(0); s.Values["c"] = V(1); s.Values["h"] = V(2); break;
            case CssColorSpace.XyzD50:
            case CssColorSpace.XyzD65:
                s.Values["x"] = V(0); s.Values["y"] = V(1); s.Values["z"] = V(2); break;
            default:
                s.Values["r"] = V(0); s.Values["g"] = V(1); s.Values["b"] = V(2); break;
        }
        s.Values["alpha"] = w.IsMissing(3) ? 0 : w.Alpha;
        return s;
    }

    static double NormalizeHue(double h)
    {
        h %= 360;
        return h < 0 ? h + 360 : h;
    }

    /// <summary><c>color()</c> 预定义空间名；未知返回 false。</summary>
    internal static bool TryColorSpace(string lower, bool allowPolar, out CssColorSpace space)
    {
        switch (lower)
        {
            case "srgb": space = CssColorSpace.Srgb; return true;
            case "srgb-linear": space = CssColorSpace.SrgbLinear; return true;
            case "display-p3": space = CssColorSpace.DisplayP3; return true;
            case "a98-rgb": space = CssColorSpace.A98Rgb; return true;
            case "prophoto-rgb": space = CssColorSpace.ProphotoRgb; return true;
            case "rec2020": space = CssColorSpace.Rec2020; return true;
            case "xyz":
            case "xyz-d65": space = CssColorSpace.XyzD65; return true;
            case "xyz-d50": space = CssColorSpace.XyzD50; return true;
        }
        if (allowPolar)
        {
            switch (lower)
            {
                case "lab": space = CssColorSpace.Lab; return true;
                case "oklab": space = CssColorSpace.Oklab; return true;
                case "hsl": space = CssColorSpace.Hsl; return true;
                case "hwb": space = CssColorSpace.Hwb; return true;
                case "lch": space = CssColorSpace.Lch; return true;
                case "oklch": space = CssColorSpace.Oklch; return true;
            }
        }
        space = default;
        return false;
    }

    /// <summary><c>color( [from &lt;color&gt;]? &lt;predefined&gt; &lt;c1&gt; &lt;c2&gt; &lt;c3&gt; [/ &lt;alpha&gt;]? )</c>。</summary>
    static bool TryParseColorFunction(IReadOnlyList<CssComponentValue> args, int depth, out ParsedColor color)
    {
        color = default;
        var r = new ValueReader(args);
        WideColor? origin = null;
        if (r.TryIdent("from"))
        {
            var ov = r.Next();
            if (ov is null || !TryParseColor(ov, depth + 1, out var o) || o.Kind != 0) return false;
            origin = o.Wide;
        }
        var name = r.PeekIdentLower();
        if (name is null || !TryColorSpace(name, allowPolar: false, out var space)) return false;
        r.Advance();
        var scope = origin is { } ow ? CreateScope(ow, space) : null;
        var comps = new List<Component>(3);
        if (!TrySplit(args, r.Position, comps, scope, out bool legacy, out var alpha) || legacy) return false;
        if (scope is not null && alpha is null) alpha = new Component(ComponentKind.Number, scope.Values["alpha"]);
        if (!TryAlpha(alpha, out var a, out bool alphaMissing)) return false;
        Span<double> v = stackalloc double[3];
        byte missing = (byte)(alphaMissing ? 8 : 0);
        for (int i = 0; i < 3; i++)
        {
            if (!Channel(comps[i], 1, false, true, out v[i], out bool m)) return false;
            if (m) missing |= (byte)(1 << i);
        }
        color = ParsedColor.Of(new WideColor(space, v[0], v[1], v[2], a, missing));
        return true;
    }

    // ================= color-mix() =================

    /// <summary>
    /// <c>color-mix( [in &lt;colorspace&gt; [&lt;hue-interpolation-method&gt;]?]? , [&lt;color&gt; &amp;&amp; &lt;percentage [0,100]&gt;?]#{2} )</c>
    /// （省略插值空间时为 oklab，CSS Color 5）。含 <c>currentColor</c> 时推迟到使用值阶段求值。
    /// </summary>
    static bool TryParseColorMix(CssFunction f, int depth, out ParsedColor color)
    {
        color = default;
        var args = f.Arguments;
        var r = new ValueReader(args);
        var space = CssColorSpace.Oklab;
        var hue = CssHueInterpolation.Shorter;
        if (r.TryIdent("in"))
        {
            var name = r.PeekIdentLower();
            if (name is null || !TryColorSpace(name, allowPolar: true, out space)) return false;
            r.Advance();
            var method = r.PeekIdentLower();
            if (method is "shorter" or "longer" or "increasing" or "decreasing")
            {
                if (space is not (CssColorSpace.Hsl or CssColorSpace.Hwb or CssColorSpace.Lch or CssColorSpace.Oklch)) return false;
                r.Advance();
                if (!r.TryIdent("hue")) return false;
                hue = method switch
                {
                    "longer" => CssHueInterpolation.Longer,
                    "increasing" => CssHueInterpolation.Increasing,
                    "decreasing" => CssHueInterpolation.Decreasing,
                    _ => CssHueInterpolation.Shorter,
                };
            }
            if (!r.TryComma()) return false;
        }
        if (!TryMixOperand(ref r, depth, out var c1, out var p1)) return false;
        if (!r.TryComma()) return false;
        if (!TryMixOperand(ref r, depth, out var c2, out var p2) || !r.AtEnd) return false;

        // 百分比规范化（CSS Color 5 §2.1）
        double a1, a2;
        if (p1 is null && p2 is null) { a1 = a2 = 50; }
        else if (p1 is null) { a2 = p2!.Value; a1 = 100 - a2; }
        else if (p2 is null) { a1 = p1.Value; a2 = 100 - a1; }
        else { a1 = p1.Value; a2 = p2.Value; }
        double sum = a1 + a2;
        if (sum <= 0) return false;
        double alphaMultiplier = sum < 100 ? sum / 100 : 1;
        double t = a2 / sum;

        if (c1.Kind == 0 && c2.Kind == 0)
        {
            color = ParsedColor.Of(Mix(c1.Wide, c2.Wide, t, alphaMultiplier, space, hue));
            return true;
        }
        color = ParsedColor.Of(new CssColorMixExpression(space, hue, c1, c2, t, alphaMultiplier, f.ToCss()));
        return true;
    }

    static bool TryMixOperand(ref ValueReader r, int depth, out ParsedColor color, out double? percent)
    {
        color = default;
        percent = null;
        bool haveColor = false;
        for (int i = 0; i < 2; i++)
        {
            var v = r.Peek();
            if (v is null || v is CssToken { Type: CssTokenType.Comma }) break;
            if (percent is null && TryPercent(v, out var p))
            {
                if (p < 0 || p > 100) return false;
                percent = p;
                r.Advance();
                continue;
            }
            if (!haveColor && TryParseColor(v, depth + 1, out color))
            {
                haveColor = true;
                r.Advance();
                continue;
            }
            return false;
        }
        return haveColor;
    }

    static bool TryPercent(CssComponentValue v, out double p)
    {
        p = 0;
        if (v is CssToken { Type: CssTokenType.Percentage } t) { p = t.Number; return true; }
        if (v is CssFunction f && CalcParser.IsMathFunction(f.Name))
        {
            var node = CalcParser.ParseComponent(f, PercentMode.Length, allowUnitlessZero: false);
            if (node is null || node.Category != CalcCategory.Percent || node.IsContextDependent) return false;
            var res = node.Compute(new LengthContext(16, 16, DefaultEnvironment));
            if (res.Tree is not null) return false;
            p = CalcMath.FiniteDouble(res.Pct);
            return true;
        }
        return false;
    }

    /// <summary>按 CSS Color 5 混合两个颜色（结果在插值空间中，未裁剪）。</summary>
    internal static WideColor Mix(in WideColor a, in WideColor b, double t, double alphaMultiplier, CssColorSpace space, CssHueInterpolation hue)
    {
        var m = CssColorMath.Interpolate(a, b, t, space, hue);
        if (alphaMultiplier >= 1) return m;
        return new WideColor(m.Space, m.C0, m.C1, m.C2, m.Alpha * alphaMultiplier, m.Missing);
    }

    /// <summary>CSS Color 4 §7.1 的 hslToRgb（s、l 为 0–1，结果 0–1）。</summary>
    internal static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
        => CssColorMath.HslToSrgb(h, s * 100, l * 100, out r, out g, out b);

    /// <summary>CSS Color 4 的全部命名色（不含 transparent）。值为 0xRRGGBB。</summary>
    internal static readonly Dictionary<string, int> NamedColors = new(StringComparer.Ordinal)
    {
        ["aliceblue"] = 0xF0F8FF, ["antiquewhite"] = 0xFAEBD7, ["aqua"] = 0x00FFFF, ["aquamarine"] = 0x7FFFD4,
        ["azure"] = 0xF0FFFF, ["beige"] = 0xF5F5DC, ["bisque"] = 0xFFE4C4, ["black"] = 0x000000,
        ["blanchedalmond"] = 0xFFEBCD, ["blue"] = 0x0000FF, ["blueviolet"] = 0x8A2BE2, ["brown"] = 0xA52A2A,
        ["burlywood"] = 0xDEB887, ["cadetblue"] = 0x5F9EA0, ["chartreuse"] = 0x7FFF00, ["chocolate"] = 0xD2691E,
        ["coral"] = 0xFF7F50, ["cornflowerblue"] = 0x6495ED, ["cornsilk"] = 0xFFF8DC, ["crimson"] = 0xDC143C,
        ["cyan"] = 0x00FFFF, ["darkblue"] = 0x00008B, ["darkcyan"] = 0x008B8B, ["darkgoldenrod"] = 0xB8860B,
        ["darkgray"] = 0xA9A9A9, ["darkgreen"] = 0x006400, ["darkgrey"] = 0xA9A9A9, ["darkkhaki"] = 0xBDB76B,
        ["darkmagenta"] = 0x8B008B, ["darkolivegreen"] = 0x556B2F, ["darkorange"] = 0xFF8C00, ["darkorchid"] = 0x9932CC,
        ["darkred"] = 0x8B0000, ["darksalmon"] = 0xE9967A, ["darkseagreen"] = 0x8FBC8F, ["darkslateblue"] = 0x483D8B,
        ["darkslategray"] = 0x2F4F4F, ["darkslategrey"] = 0x2F4F4F, ["darkturquoise"] = 0x00CED1, ["darkviolet"] = 0x9400D3,
        ["deeppink"] = 0xFF1493, ["deepskyblue"] = 0x00BFFF, ["dimgray"] = 0x696969, ["dimgrey"] = 0x696969,
        ["dodgerblue"] = 0x1E90FF, ["firebrick"] = 0xB22222, ["floralwhite"] = 0xFFFAF0, ["forestgreen"] = 0x228B22,
        ["fuchsia"] = 0xFF00FF, ["gainsboro"] = 0xDCDCDC, ["ghostwhite"] = 0xF8F8FF, ["gold"] = 0xFFD700,
        ["goldenrod"] = 0xDAA520, ["gray"] = 0x808080, ["green"] = 0x008000, ["greenyellow"] = 0xADFF2F,
        ["grey"] = 0x808080, ["honeydew"] = 0xF0FFF0, ["hotpink"] = 0xFF69B4, ["indianred"] = 0xCD5C5C,
        ["indigo"] = 0x4B0082, ["ivory"] = 0xFFFFF0, ["khaki"] = 0xF0E68C, ["lavender"] = 0xE6E6FA,
        ["lavenderblush"] = 0xFFF0F5, ["lawngreen"] = 0x7CFC00, ["lemonchiffon"] = 0xFFFACD, ["lightblue"] = 0xADD8E6,
        ["lightcoral"] = 0xF08080, ["lightcyan"] = 0xE0FFFF, ["lightgoldenrodyellow"] = 0xFAFAD2, ["lightgray"] = 0xD3D3D3,
        ["lightgreen"] = 0x90EE90, ["lightgrey"] = 0xD3D3D3, ["lightpink"] = 0xFFB6C1, ["lightsalmon"] = 0xFFA07A,
        ["lightseagreen"] = 0x20B2AA, ["lightskyblue"] = 0x87CEFA, ["lightslategray"] = 0x778899, ["lightslategrey"] = 0x778899,
        ["lightsteelblue"] = 0xB0C4DE, ["lightyellow"] = 0xFFFFE0, ["lime"] = 0x00FF00, ["limegreen"] = 0x32CD32,
        ["linen"] = 0xFAF0E6, ["magenta"] = 0xFF00FF, ["maroon"] = 0x800000, ["mediumaquamarine"] = 0x66CDAA,
        ["mediumblue"] = 0x0000CD, ["mediumorchid"] = 0xBA55D3, ["mediumpurple"] = 0x9370DB, ["mediumseagreen"] = 0x3CB371,
        ["mediumslateblue"] = 0x7B68EE, ["mediumspringgreen"] = 0x00FA9A, ["mediumturquoise"] = 0x48D1CC, ["mediumvioletred"] = 0xC71585,
        ["midnightblue"] = 0x191970, ["mintcream"] = 0xF5FFFA, ["mistyrose"] = 0xFFE4E1, ["moccasin"] = 0xFFE4B5,
        ["navajowhite"] = 0xFFDEAD, ["navy"] = 0x000080, ["oldlace"] = 0xFDF5E6, ["olive"] = 0x808000,
        ["olivedrab"] = 0x6B8E23, ["orange"] = 0xFFA500, ["orangered"] = 0xFF4500, ["orchid"] = 0xDA70D6,
        ["palegoldenrod"] = 0xEEE8AA, ["palegreen"] = 0x98FB98, ["paleturquoise"] = 0xAFEEEE, ["palevioletred"] = 0xDB7093,
        ["papayawhip"] = 0xFFEFD5, ["peachpuff"] = 0xFFDAB9, ["peru"] = 0xCD853F, ["pink"] = 0xFFC0CB,
        ["plum"] = 0xDDA0DD, ["powderblue"] = 0xB0E0E6, ["purple"] = 0x800080, ["rebeccapurple"] = 0x663399,
        ["red"] = 0xFF0000, ["rosybrown"] = 0xBC8F8F, ["royalblue"] = 0x4169E1, ["saddlebrown"] = 0x8B4513,
        ["salmon"] = 0xFA8072, ["sandybrown"] = 0xF4A460, ["seagreen"] = 0x2E8B57, ["seashell"] = 0xFFF5EE,
        ["sienna"] = 0xA0522D, ["silver"] = 0xC0C0C0, ["skyblue"] = 0x87CEEB, ["slateblue"] = 0x6A5ACD,
        ["slategray"] = 0x708090, ["slategrey"] = 0x708090, ["snow"] = 0xFFFAFA, ["springgreen"] = 0x00FF7F,
        ["steelblue"] = 0x4682B4, ["tan"] = 0xD2B48C, ["teal"] = 0x008080, ["thistle"] = 0xD8BFD8,
        ["tomato"] = 0xFF6347, ["turquoise"] = 0x40E0D0, ["violet"] = 0xEE82EE, ["wheat"] = 0xF5DEB3,
        ["white"] = 0xFFFFFF, ["whitesmoke"] = 0xF5F5F5, ["yellow"] = 0xFFFF00, ["yellowgreen"] = 0x9ACD32,
    };
}
