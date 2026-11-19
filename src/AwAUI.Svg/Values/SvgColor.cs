using System;
using System.Collections.Generic;

namespace AwAUI.Svg.Values;

/// <summary>
/// CSS Color 3 颜色语法（另接受 CSS Color 4 的 #rgba / #rrggbbaa 与空格分隔的 rgb()/hsl()）：
/// #hex、rgb()/rgba()、hsl()/hsla()、148 个命名色（含 rebeccapurple）、transparent。
/// currentColor 由调用方单独处理。SVG 1.1 允许在颜色后跟 icc-color(...)，这里忽略它。
/// </summary>
internal static class SvgColor
{
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (text == null) return false;
        var s = text.AsSpan().Trim();
        int icc = IndexOfIgnoreCase(s, "icc-color(");
        if (icc > 0) s = s.Slice(0, icc).TrimEnd();
        if (s.Length == 0) return false;

        if (s[0] == '#') return TryParseHex(s.Slice(1), out color);

        int paren = s.IndexOf('(');
        if (paren > 0)
        {
            if (s[s.Length - 1] != ')') return false;
            var name = s.Slice(0, paren).Trim();
            var args = s.Slice(paren + 1, s.Length - paren - 2);
            if (EqualsIgnoreCase(name, "rgb") || EqualsIgnoreCase(name, "rgba")) return TryParseRgb(args, out color);
            if (EqualsIgnoreCase(name, "hsl") || EqualsIgnoreCase(name, "hsla")) return TryParseHsl(args, out color);
            return false;
        }

        if (s.Length > 20) return false;
        var lower = s.ToString().ToLowerInvariant();
        if (lower == "transparent") { color = Color.Transparent; return true; }
        if (NamedColors.TryGetValue(lower, out int rgb))
        {
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        return false;
    }

    static bool TryParseHex(ReadOnlySpan<char> s, out Color color)
    {
        color = default;
        if (s.Length is not (3 or 4 or 6 or 8)) return false;
        uint v = 0;
        foreach (char c in s)
        {
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0) return false;
            v = (v << 4) | (uint)d;
        }
        switch (s.Length)
        {
            case 3:
                color = Color.FromRgb((byte)((v >> 8 & 0xF) * 17), (byte)((v >> 4 & 0xF) * 17), (byte)((v & 0xF) * 17));
                break;
            case 4:
                color = Color.FromRgba((byte)((v >> 12 & 0xF) * 17), (byte)((v >> 8 & 0xF) * 17), (byte)((v >> 4 & 0xF) * 17), (byte)((v & 0xF) * 17));
                break;
            case 6:
                color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
                break;
            default:
                color = Color.FromRgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
                break;
        }
        return true;
    }

    /// <summary>拆分函数参数：逗号语法或空格语法（可带 / alpha）。最多 4 个分量。</summary>
    static bool SplitArgs(ReadOnlySpan<char> args, Span<(float Value, bool Percent)> parts, out int count)
    {
        count = 0;
        int pos = 0;
        SvgNumber.SkipWsp(args, ref pos);
        bool? commas = null;
        bool slashSeen = false;
        while (pos < args.Length)
        {
            if (count == 4) return false;
            if (!SvgNumber.TryScan(args, ref pos, out float v) || !SvgNumber.IsFinite(v)) return false;
            bool pct = false;
            if (pos < args.Length && args[pos] == '%') { pct = true; pos++; }
            else if (pos + 3 <= args.Length && EqualsIgnoreCase(args.Slice(pos, 3), "deg")) pos += 3;
            parts[count++] = (v, pct);
            SvgNumber.SkipWsp(args, ref pos);
            if (pos >= args.Length) break;
            char c = args[pos];
            if (c == ',')
            {
                if (commas == false) return false;
                commas = true;
                pos++;
            }
            else if (c == '/')
            {
                if (commas == true || slashSeen || count != 3) return false;
                slashSeen = true;
                pos++;
            }
            else
            {
                if (commas == true) return false;
                commas = false;
            }
            SvgNumber.SkipWsp(args, ref pos);
            if (pos >= args.Length) return false; // 结尾多余的分隔符
        }
        if (commas == false && count == 4 && !slashSeen) return false;
        return count is 3 or 4;
    }

    static float Alpha((float Value, bool Percent) a) => Clamp01(a.Percent ? a.Value / 100f : a.Value);

    static bool TryParseRgb(ReadOnlySpan<char> args, out Color color)
    {
        color = default;
        Span<(float, bool)> p = stackalloc (float, bool)[4];
        if (!SplitArgs(args, p, out int n)) return false;
        // CSS Color 3：三个分量要么都是百分比，要么都是数字
        if (p[0].Item2 != p[1].Item2 || p[1].Item2 != p[2].Item2) return false;
        float Ch((float V, bool P) x) => Clamp01(x.P ? x.V / 100f : x.V / 255f);
        color = new Color(Ch(p[0]), Ch(p[1]), Ch(p[2]), n == 4 ? Alpha(p[3]) : 1f);
        return true;
    }

    static bool TryParseHsl(ReadOnlySpan<char> args, out Color color)
    {
        color = default;
        Span<(float, bool)> p = stackalloc (float, bool)[4];
        if (!SplitArgs(args, p, out int n)) return false;
        if (p[0].Item2 || !p[1].Item2 || !p[2].Item2) return false;
        double h = ((p[0].Item1 % 360) + 360) % 360;
        double sat = Clamp01(p[1].Item1 / 100f), l = Clamp01(p[2].Item1 / 100f);
        double F(double k0)
        {
            double k = (k0 + h / 30) % 12;
            double a = sat * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }
        color = new Color((float)F(0), (float)F(8), (float)F(4), n == 4 ? Alpha(p[3]) : 1f);
        return true;
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    internal static bool EqualsIgnoreCase(ReadOnlySpan<char> a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[i])) return false;
        return true;
    }

    static int IndexOfIgnoreCase(ReadOnlySpan<char> s, string token)
    {
        for (int i = 0; i + token.Length <= s.Length; i++)
            if (EqualsIgnoreCase(s.Slice(i, token.Length), token)) return i;
        return -1;
    }

    /// <summary>CSS Color 4 的全部命名色（不含 transparent）。值为 0xRRGGBB。</summary>
    static readonly Dictionary<string, int> NamedColors = new(StringComparer.Ordinal)
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
