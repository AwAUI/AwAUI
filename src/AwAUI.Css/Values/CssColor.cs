using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>
/// 颜色的计算值：具体的 sRGB 颜色，或保留为关键字的 <c>currentColor</c>（使用时取元素的 <c>color</c>，design/08）。
/// </summary>
public readonly struct CssColor : IEquatable<CssColor>
{
    public CssColor(Color value)
    {
        Value = value;
        IsCurrentColor = false;
    }

    CssColor(bool currentColor)
    {
        Value = default;
        IsCurrentColor = currentColor;
    }

    /// <summary><c>currentColor</c>。</summary>
    public static CssColor CurrentColor => new(true);

    public static CssColor Transparent => new(Color.Transparent);

    /// <summary>具体颜色；<see cref="IsCurrentColor"/> 为真时无意义。</summary>
    public Color Value { get; }

    public bool IsCurrentColor { get; }

    /// <summary>得到使用值：<c>currentColor</c> 取 <paramref name="currentColor"/>。</summary>
    public Color Resolve(Color currentColor) => IsCurrentColor ? currentColor : Value;

    public bool Equals(CssColor other) => IsCurrentColor == other.IsCurrentColor && Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is CssColor c && Equals(c);
    public override int GetHashCode() => IsCurrentColor ? 1 : Value.GetHashCode();
    public static bool operator ==(CssColor a, CssColor b) => a.Equals(b);
    public static bool operator !=(CssColor a, CssColor b) => !a.Equals(b);

    /// <summary>按 CSSOM 的规则序列化：不透明为 <c>rgb(r, g, b)</c>，否则 <c>rgba(r, g, b, a)</c>。</summary>
    public override string ToString()
    {
        if (IsCurrentColor) return "currentcolor";
        var c = Value;
        int r = To8(c.R), g = To8(c.G), b = To8(c.B);
        if (c.A >= 1f) return string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", r, g, b);
        double a = Math.Round(Math.Max(0, Math.Min(1, (double)c.A)), 3);
        return string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3})", r, g, b, CssSerializer.FormatNumber(a));
    }

    static int To8(float v) => (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
}

/// <summary>颜色解析：十六进制、<c>rgb()/rgba()/hsl()/hsla()</c>（逗号与现代空格语法、<c>none</c>）、命名色、<c>transparent</c>、<c>currentColor</c>。</summary>
public static class CssColorParser
{
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
        color = default;
        switch (value)
        {
            case CssToken { Type: CssTokenType.Hash } h:
                if (!TryParseHex(h.Value, out var c)) return false;
                color = new CssColor(c);
                return true;
            case CssToken { Type: CssTokenType.Ident } id:
            {
                var name = CssSerializer.ToAsciiLower(id.Value);
                if (name == "currentcolor") { color = CssColor.CurrentColor; return true; }
                if (name == "transparent") { color = CssColor.Transparent; return true; }
                if (NamedColors.TryGetValue(name, out var rgb))
                {
                    color = new CssColor(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                    return true;
                }
                return false;
            }
            case CssFunction f:
                switch (CssSerializer.ToAsciiLower(f.Name))
                {
                    case "rgb":
                    case "rgba":
                        return TryParseRgb(f.Arguments, out color);
                    case "hsl":
                    case "hsla":
                        return TryParseHsl(f.Arguments, out color);
                    default:
                        return false;
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

    /// <summary>颜色函数的一个分量：数、百分比或 <c>none</c>（视为 0）。</summary>
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

    /// <summary>把参数拆成分量：返回 (分量列表, 是否逗号语法, alpha)。calc() 在分量中按数值求值。</summary>
    static bool TrySplit(IReadOnlyList<CssComponentValue> args, List<Component> comps, out bool legacy, out Component? alpha)
    {
        legacy = false;
        alpha = null;
        var r = new ValueReader(args);
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
            if (!TryComponent(v, out var c)) return false;
            if (sawSlash) { if (alpha.HasValue) return false; alpha = c; }
            else if (sawComma && index == 3) alpha = c;
            else comps.Add(c);
            index++;
            if (index > 4) return false;
        }
        legacy = sawComma;
        return comps.Count == 3;
    }

    static bool TryComponent(CssComponentValue v, out Component c)
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
            case CssFunction f when CalcParser.IsMathFunction(f.Name):
            {
                var node = CalcParser.ParseComponent(f, PercentMode.Length, allowUnitlessZero: false);
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

    static readonly CssEnvironment DefaultEnvironment = new();

    static bool TryAlpha(Component? alpha, out double a)
    {
        a = 1;
        if (alpha is not { } c) return true;
        switch (c.Kind)
        {
            case ComponentKind.Number: a = c.Value; break;
            case ComponentKind.Percent: a = c.Value / 100; break;
            case ComponentKind.None: a = 0; break;
            default: return false;
        }
        a = CalcMath.Clamp(CalcMath.FiniteDouble(a), 0, 1);
        return true;
    }

    static bool TryParseRgb(IReadOnlyList<CssComponentValue> args, out CssColor color)
    {
        color = default;
        var comps = new List<Component>(3);
        if (!TrySplit(args, comps, out bool legacy, out var alpha)) return false;
        // 逗号语法：三个分量必须同为数或同为百分比，且不允许 none
        if (legacy)
        {
            var k = comps[0].Kind;
            if (k == ComponentKind.None || k == ComponentKind.Angle) return false;
            foreach (var c in comps) if (c.Kind != k) return false;
            if (alpha is { Kind: ComponentKind.None }) return false;
        }
        Span<double> rgb = stackalloc double[3];
        for (int i = 0; i < 3; i++)
        {
            var c = comps[i];
            double v = c.Kind switch
            {
                ComponentKind.Number => c.Value,
                ComponentKind.Percent => c.Value * 255 / 100,
                ComponentKind.None => 0,
                _ => double.NaN,
            };
            if (double.IsNaN(v) && c.Kind == ComponentKind.Angle) return false;
            rgb[i] = CalcMath.Clamp(CalcMath.FiniteDouble(v), 0, 255) / 255;
        }
        if (!TryAlpha(alpha, out var a)) return false;
        color = new CssColor(new Color((float)rgb[0], (float)rgb[1], (float)rgb[2], (float)a));
        return true;
    }

    static bool TryParseHsl(IReadOnlyList<CssComponentValue> args, out CssColor color)
    {
        color = default;
        var comps = new List<Component>(3);
        if (!TrySplit(args, comps, out bool legacy, out var alpha)) return false;
        var hc = comps[0];
        if (hc.Kind == ComponentKind.Percent) return false;
        double h = hc.Kind == ComponentKind.None ? 0 : hc.Value;
        double s, l;
        if (legacy)
        {
            if (hc.Kind == ComponentKind.None) return false;
            if (comps[1].Kind != ComponentKind.Percent || comps[2].Kind != ComponentKind.Percent) return false;
            if (alpha is { Kind: ComponentKind.None }) return false;
        }
        if (!TrySatLight(comps[1], out s) || !TrySatLight(comps[2], out l)) return false;
        if (!TryAlpha(alpha, out var a)) return false;
        h = CalcMath.FiniteDouble(h) % 360;
        if (h < 0) h += 360;
        s = CalcMath.Clamp(s, 0, 100) / 100;
        l = CalcMath.Clamp(l, 0, 100) / 100;
        HslToRgb(h, s, l, out var r, out var g, out var b);
        color = new CssColor(new Color((float)r, (float)g, (float)b, (float)a));
        return true;
    }

    static bool TrySatLight(Component c, out double v)
    {
        v = 0;
        switch (c.Kind)
        {
            case ComponentKind.Percent:
            case ComponentKind.Number: v = CalcMath.FiniteDouble(c.Value); return true;
            case ComponentKind.None: return true;
            default: return false;
        }
    }

    /// <summary>CSS Color 4 §7.1 的 hslToRgb。</summary>
    internal static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        r = F(0);
        g = F(8);
        b = F(4);

        double F(double n)
        {
            double k = (n + h / 30) % 12;
            double a = s * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }
    }

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
